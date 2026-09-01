namespace Benchmark.Decoys;

public sealed class ExportEnvelope
{
    public byte[] Encode(string value) => System.Text.Encoding.UTF8.GetBytes(value);
    public string Format => "utf-8";
}
