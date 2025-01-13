using System.Text;
using FetchHotfixCS;
using Newtonsoft.Json;

namespace FetchHotfix;

class Program
{
    static string ReadString(byte[] buffer, int offset = 0)
    {
        int length = buffer[offset];
        return Encoding.UTF8.GetString(buffer, offset + 1, length);
    }

    static byte[] StripEmptyBytes(byte[] buffer)
    {
        int end = buffer.Length;
        while (end > 0 && buffer[end - 1] == 0x00)
        {
            end--;
        }
        return buffer.Take(end).ToArray();
    }

    static int LastIndexOf(byte[] buffer, byte[] pattern)
    {
        for (int i = buffer.Length - pattern.Length; i >= 0; i--)
        {
            if (buffer.Skip(i).Take(pattern.Length).SequenceEqual(pattern))
                return i;
        }
        return -1;
    }

    static byte[] LastIndexOf(byte[] buffer, byte delimiter)
    {
        List<byte[]> parts = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == delimiter)
            {
                parts.Add(buffer[start..i]);
                start = i + 1;
            }
        }
        if (start < buffer.Length)
        {
            parts.Add(buffer[start..]);
        }
        return parts.LastOrDefault()!;
    }

    static int ReadUint24BE(byte[] buffer, int offset = 0)
    {
        return (buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2];
    }

    static bool SeedSanityCheck(string dispatchSeed)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(dispatchSeed, "^[0-9A-Fa-f]*$");
    }

    static List<byte[]> SplitBuffer(byte[] buffer, byte delimiter)
    {
        List<byte[]> result = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == delimiter)
            {
                if (i > start)
                {
                    result.Add(buffer[start..i]);
                }
                start = i + 1;
            }
        }
        if (start < buffer.Length)
        {
            result.Add(buffer[start..]);
        }
        return result;
    }

    static (string version, string seed)? GetDispatchSeed(List<byte[]> bufferSplits, string constructedString)
    {
        for (int i = 1; i < bufferSplits.Count; i++)
        {
            if (bufferSplits[i].Length < 2)
            {
                continue;
            }

            if (ReadString(bufferSplits[i]).StartsWith(constructedString))
            {
                string seed = ReadString(bufferSplits[i - 1]);
                if (SeedSanityCheck(seed))
                {
                    return (ReadString(bufferSplits[i]), seed);
                }
                return null;
            }
        }
        return null;
    }

    public static void PrintByteArray(byte[] bytes)
    {
        var sb = new StringBuilder("new byte[] { ");
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("X") + " ");
        }
        sb.Append("}");
        Console.WriteLine(sb.ToString());
    }

    static async Task Main(string[] args)
    {
        string starRailDir = args[0];

        byte[] bufferBinaryVersion = File.ReadAllBytes(Path.Combine(starRailDir, "StarRail_Data", "StreamingAssets", "BinaryVersion.bytes"));
        byte[] bufferClientConfig = File.ReadAllBytes(Path.Combine(starRailDir, "StarRail_Data", "StreamingAssets", "ClientConfig.bytes"));

        byte[] bufferClientConfigParts = StripEmptyBytes(bufferClientConfig);
        string queryDispatchPre = ReadString(LastIndexOf(bufferClientConfigParts, 0x00), 0);

        byte[] zeroPattern = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        int lastIndex = LastIndexOf(bufferBinaryVersion, zeroPattern);
        byte[] lastBuffer = bufferBinaryVersion[(lastIndex + zeroPattern.Length)..];

        var bufferSplits = SplitBuffer(lastBuffer, 0x00).Where(buffer => buffer.Length > 0).ToList();
        string branch = ReadString(bufferBinaryVersion, 1);
        int revision = ReadUint24BE(bufferSplits[0]);
        string time = ReadString(bufferSplits[1]);
        var dispatchSeed = GetDispatchSeed(bufferSplits, $"{time}-{branch}");

        if (dispatchSeed == null)
        {
            Console.WriteLine("Unable to parse dispatch seed for this game version, please ensure you entered the correct game path and try again.");
            Environment.Exit(1);
        }

        Console.WriteLine($"Dispatch Seed: {dispatchSeed.Value.seed}");

        string[] versionSplit = dispatchSeed.Value.version.Split('-');
        string version = versionSplit[4];
        string build = versionSplit[5];

        Console.WriteLine($"Version: {version}");
        Console.WriteLine($"Build: {build}");

        string urlStart;
        ReturnData returnData = new();

        try
        {
            using HttpClient client = new HttpClient();
            string urlDispatch = $"{queryDispatchPre}?version={version}&language_type=3&platform_type=3&channel_id=1&sub_channel_id=1&is_new_format=1";
            HttpResponseMessage responseDispatch = await client.GetAsync(urlDispatch);
            byte[] protoBytesDispatch = Convert.FromBase64String(await responseDispatch.Content.ReadAsStringAsync());
            Dispatch decodedDispatch = Dispatch.Parser.ParseFrom(protoBytesDispatch);
            urlStart = decodedDispatch.RegionList.First().DispatchUrl;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error fetching dispatch: " + ex);
            return;
        }

        string url = $"{urlStart}?version={version}&platform_type=1&language_type=3&dispatch_seed={dispatchSeed.Value.seed}&channel_id=1&sub_channel_id=1&is_need_url=1";

        try
        {
            using HttpClient client = new HttpClient();
            HttpResponseMessage response = await client.GetAsync(url);
            byte[] protoBytes = Convert.FromBase64String(await response.Content.ReadAsStringAsync());
            ProtobufDecoder protobufDecoder = new(protoBytes);
            DecodingResult decoded = protobufDecoder.Decode()!;
            SimpleDecodingResult simplified = ProtobufUtils.Simplify(decoded);
            foreach (var field in simplified.Fields)
            {
                string val = field.Value.ToString()!;
                if (val.Contains("/asb/"))
                {
                    returnData.assetBundleUrl = val;
                }
                else if (val.Contains("/design_data/"))
                {
                    returnData.exResourceUrl = val;
                }
                else if (val.Contains("/lua/"))
                {
                    returnData.luaUrl = val;
                }
                else if (val.Contains("/ifix/"))
                {
                    returnData.ifixUrl = val;
                }
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine("Error fetching data: " + ex);
            return;
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "hotfix.json"), JsonConvert.SerializeObject(returnData, Newtonsoft.Json.Formatting.Indented));
        Console.WriteLine("Data written to hotfix.json");
    }
}

public class ReturnData
{
    public string assetBundleUrl { get; set; } = "";
    public string exResourceUrl { get; set; } = "";
    public string luaUrl { get; set; } = "";
    public string ifixUrl { get; set; } = "";
    public int customMdkResVersion { get; set; } = 0;
    public int customIfixVersion { get; set; } = 0;
}