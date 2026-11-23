using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Arboreal.InferenceRouter.Cli;

internal sealed class AirTuneTextGenerator
{
    internal const string Id = "air-tune/text-generator/v1";
    internal const int MaxCandidateBytes = 8_388_608;
    internal const int MaxRecordCount = MaxCandidateBytes / 67;

    private const string NoncePrefix = "nonce ";
    private const string Instruction = "Copy the numbered records below in order.\n";
    private static readonly int HeaderBytes = NoncePrefix.Length + 64 + 1 + Instruction.Length;
    private readonly string _fields;
    private readonly string _domain;

    internal static string DefinitionSha256 { get; } = HashDefinition();

    internal string Nonce => Hash(_domain + "-nonce", 0);

    internal AirTuneTextGenerator(string seed, string tier, int concurrency, int wave, int worker)
    {
        if (!AirTuneRecord.IsDigest(seed))
            throw new ArgumentException("Invalid workload seed.", nameof(seed));
        if (!IsIdentity(tier))
            throw new ArgumentException("Invalid workload tier.", nameof(tier));
        if (concurrency is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        if (wave is < 0 or > 3 || (wave == 0 && concurrency != 1))
            throw new ArgumentOutOfRangeException(nameof(wave));
        if (worker < 1 || worker > concurrency)
            throw new ArgumentOutOfRangeException(nameof(worker));

        _domain = wave == 0 ? "warmup" : "grid";
        if (wave == 0)
            seed = Sha256($"{Id}/warmup-seed\n{seed}\n{tier}\n");
        _fields = string.Create(CultureInfo.InvariantCulture, $"{seed}\n{tier}\n{concurrency}\n{wave}\n{worker}\n");
    }

    internal static bool IsIdentity(string value)
    {
        if (value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0]))
            return false;

        foreach (var ch in value.AsSpan(1))
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or ':' or '@' or '/' or '+' or '-'))
                return false;
        return true;
    }

    internal byte[] Candidate(int fullRecords, int tailBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fullRecords);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fullRecords, MaxRecordCount);
        ArgumentOutOfRangeException.ThrowIfNegative(tailBytes);

        var next = fullRecords + 1;
        var nextLength = RecordByteLength(next);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tailBytes, nextLength);
        var length = checked(PrefixByteLength(fullRecords) + tailBytes);
        if (length > MaxCandidateBytes)
            throw new ArgumentOutOfRangeException(nameof(fullRecords), "Candidate exceeds its byte limit.");
        if (tailBytes == nextLength)
        {
            fullRecords = next;
            tailBytes = 0;
        }

        var text = new StringBuilder(length).Append(NoncePrefix).Append(Nonce).Append('\n').Append(Instruction);
        for (var line = 1; line <= fullRecords; line++)
            text.Append(Record(line));
        if (tailBytes != 0)
            text.Append(Record(next).AsSpan(0, tailBytes));
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    internal static int PrefixByteLength(int records)
    {
        var bytes = HeaderBytes;
        var width = 1;
        for (var start = 1; start <= records; start *= 10)
        {
            var end = Math.Min(records, start * 10 - 1);
            bytes = checked(bytes + (end - start + 1) * (width + 66));
            width++;
        }
        return bytes;
    }

    internal static int RecordByteLength(int line)
    {
        var width = 1;
        for (; line >= 10; line /= 10)
            width++;
        return width + 66;
    }

    private string Record(int line) =>
        string.Create(CultureInfo.InvariantCulture, $"{line} {Hash(_domain + "-record", line)}\n");

    private string Hash(string domain, int line) =>
        Sha256(string.Create(CultureInfo.InvariantCulture, $"{Id}/{domain}\n{_fields}{line}\n"));

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    private static string HashDefinition()
    {
        using var bytes = typeof(AirTuneTextGenerator).Assembly.GetManifestResourceStream("AirTune/TextGenerator/v1.txt")
            ?? throw new InvalidOperationException("Embedded workload generator definition is missing.");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
