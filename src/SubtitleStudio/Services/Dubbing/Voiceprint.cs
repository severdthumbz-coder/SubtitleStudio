using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>A voiceprint model: a stretch of speech in, a vector out; the same voice gives similar vectors.</summary>
public interface IVoiceprintModel : IDisposable
{
    /// <summary>The voiceprint (unit length) of 16 kHz mono speech. Safe to call from several threads.</summary>
    float[] Embed(ReadOnlySpan<float> samples);
}

/// <summary>
/// WeSpeaker ResNet34-LM (trained on VoxCeleb; CC BY 4.0), the ONNX export from the sherpa-onnx project,
/// through ONNX Runtime on the processor (26 MB; a line takes a few hundredths of a second). Features are
/// computed in the app (Fbank).
/// </summary>
public sealed class OnnxVoiceprintModel : IVoiceprintModel
{
    private readonly InferenceSession _session;

    public OnnxVoiceprintModel(string modelPath)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            IntraOpNumThreads = 2,
        };
        _session = new InferenceSession(modelPath, options);
    }

    public float[] Embed(ReadOnlySpan<float> samples)
    {
        var feats = Fbank.Compute(samples);
        int frames = feats.GetLength(0);
        if (frames == 0) return Array.Empty<float>();
        var flat = new float[frames * Fbank.Bins];
        Buffer.BlockCopy(feats, 0, flat, 0, flat.Length * sizeof(float));
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("feats", new DenseTensor<float>(flat, new[] { 1, frames, Fbank.Bins })),
        };
        using var results = _session.Run(inputs);
        return Voiceprints.Normalize(results.First().AsEnumerable<float>().ToArray());
    }

    public void Dispose() => _session.Dispose();
}

public static class Voiceprints
{
    public static float[] Normalize(float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n <= 0) return v;
        return v.Select(x => (float)(x / n)).ToArray();
    }

    /// <summary>Cosine similarity of two unit vectors (1 = the same direction).</summary>
    public static double Similarity(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>The average direction of several unit vectors, itself of unit length.</summary>
    public static float[] Mean(IEnumerable<float[]> vectors)
    {
        float[]? sum = null;
        foreach (var v in vectors)
        {
            sum ??= new float[v.Length];
            for (int i = 0; i < v.Length; i++) sum[i] += v[i];
        }
        return sum is null ? Array.Empty<float>() : Normalize(sum);
    }
}
