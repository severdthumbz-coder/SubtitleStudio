using System.Text;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Makes Kokoro runnable on DirectML, in memory, when it is loaded (the file on disk is not changed).
///
/// DirectML fails on Kokoro's transposed convolutions ("Non-zero status code returned while running
/// ConvTranspose node /encoder/F0.1/pool/ConvTranspose" on an RX 6850M XT). Each 1-D ConvTranspose is
/// rewritten into the identical plain convolution:
///   insert s-1 zeros after every input step (Unsqueeze, Pad, Reshape), pad both ends, then Conv with
///   the kernel flipped and its in/out channels swapped per group, stride 1.
/// Output length: s*T + (k-1-p0) + (k-1-p1+op-(s-1)) - k + 1 = (T-1)*s - p0 - p1 + k + op, the same as
/// ConvTranspose's. The weights are constants in the file, so the new kernel is computed here once.
/// Everything else is copied byte for byte.
///
/// Works on the protobuf wire format directly (ModelProto.graph = 7; GraphProto: node = 1,
/// initializer = 5; NodeProto: input = 1, output = 2, name = 3, op_type = 4, attribute = 5, domain = 7;
/// TensorProto: dims = 1, data_type = 2, float_data = 4, name = 8, raw_data = 9), so no ONNX library is needed.
/// </summary>
public static class KokoroDmlPatcher
{
    private readonly record struct Field(int Number, int WireType, int Start, int ValueStart, int Length, int End, ulong Varint);

    private sealed class Node
    {
        public List<string> Inputs = new();
        public List<string> Outputs = new();
        public string Name = string.Empty;
        public string OpType = string.Empty;
        public string Domain = string.Empty;
        public Dictionary<string, long[]> Ints = new();
        public HashSet<string> OtherAttributes = new();
    }

    private sealed record Tensor(string Name, long[] Dims, int DataType, float[]? Floats);

    /// <summary>The patched model and how many ConvTranspose nodes were rewritten (0: returned unchanged).</summary>
    public static byte[] Patch(byte[] model, out int rewritten)
    {
        rewritten = 0;
        var top = ReadFields(model, 0, model.Length);
        var graphField = top.FirstOrDefault(f => f.Number == 7 && f.WireType == 2);
        if (graphField.Length == 0) return model;

        var graphFields = ReadFields(model, graphField.ValueStart, graphField.End);
        var nodes = new List<(int Index, Node Node)>();
        var tensors = new Dictionary<string, (int Index, Tensor Tensor)>();
        var uses = new Dictionary<string, int>();
        for (int i = 0; i < graphFields.Count; i++)
        {
            var f = graphFields[i];
            if (f.Number == 1 && f.WireType == 2)
            {
                var n = ParseNode(model, f);
                nodes.Add((i, n));
                foreach (var input in n.Inputs) uses[input] = uses.GetValueOrDefault(input) + 1;
            }
            else if (f.Number == 5 && f.WireType == 2 && ParseTensor(model, f) is { } t) tensors[t.Name] = (i, t);
        }

        var replace = new Dictionary<int, byte[]>();
        var added = new MemoryStream();
        int counter = 0;
        foreach (var (index, n) in nodes)
        {
            if (n.OpType != "ConvTranspose" || n.Domain.Length > 0 || n.Inputs.Count < 2 || n.Outputs.Count != 1) continue;
            if (n.OtherAttributes.Count > 0) continue; // auto_pad, output_shape: not handled, left as is
            if (!tensors.TryGetValue(n.Inputs[1], out var w) || w.Tensor.Floats is not { } wt || w.Tensor.Dims.Length != 3) continue;
            long group = n.Ints.TryGetValue("group", out var g) ? g[0] : 1;
            long cin = w.Tensor.Dims[0], coutG = w.Tensor.Dims[1], k = w.Tensor.Dims[2];
            long s = n.Ints.TryGetValue("strides", out var st) ? st[0] : 1;
            long[] pads = n.Ints.TryGetValue("pads", out var p) && p.Length == 2 ? p : new long[] { 0, 0 };
            long op = n.Ints.TryGetValue("output_padding", out var o) ? o[0] : 0;
            long dil = n.Ints.TryGetValue("dilations", out var d) ? d[0] : 1;
            if (dil != 1 || group < 1 || cin % group != 0 || (n.Ints.TryGetValue("kernel_shape", out var ks) && ks[0] != k)) continue;
            long left = k - 1 - pads[0], right = k - 1 - pads[1] + op - (s - 1);
            if (left < 0 || right < 0) continue;

            // Conv kernel [G*Cout_g, Cin_g, k] from the transposed one [Cin, Cout_g, k]: per group, in and out swapped, flipped in time.
            long cinG = cin / group;
            var wc = new float[wt.Length];
            for (long gi = 0; gi < group; gi++)
                for (long oi = 0; oi < coutG; oi++)
                    for (long ii = 0; ii < cinG; ii++)
                        for (long j = 0; j < k; j++)
                            wc[((gi * coutG + oi) * cinG + ii) * k + j] = wt[((gi * cinG + ii) * coutG + oi) * k + (k - 1 - j)];

            string tag = $"__dml_ct{counter++}", x = n.Inputs[0], y = n.Outputs[0];
            string wName = n.Inputs[1] + tag + "_conv";
            WriteFloatTensor(added, wName, new[] { group * coutG, cinG, k }, wc);
            var sb = new MemoryStream();
            string current = x;
            if (s > 1)
            {
                string axes = tag + "_axes", pz = tag + "_zeros", shape = tag + "_shape";
                WriteInt64Tensor(added, axes, new long[] { 1 }, new long[] { 3 });
                WriteInt64Tensor(added, pz, new long[] { 8 }, new long[] { 0, 0, 0, 0, 0, 0, 0, s - 1 });
                WriteInt64Tensor(added, shape, new long[] { 3 }, new long[] { 0, 0, -1 });
                WriteNode(sb, "Unsqueeze", new[] { x, axes }, new[] { y + tag + "_u" }, n.Name + tag + "_Unsqueeze", null);
                WriteNode(sb, "Pad", new[] { y + tag + "_u", pz }, new[] { y + tag + "_z" }, n.Name + tag + "_Zeros", null);
                WriteNode(sb, "Reshape", new[] { y + tag + "_z", shape }, new[] { y + tag + "_up" }, n.Name + tag + "_Reshape", null);
                current = y + tag + "_up";
            }
            if (left > 0 || right > 0)
            {
                string pe = tag + "_edges";
                WriteInt64Tensor(added, pe, new long[] { 6 }, new long[] { 0, 0, left, 0, 0, right });
                WriteNode(sb, "Pad", new[] { current, pe }, new[] { y + tag + "_p" }, n.Name + tag + "_Pad", null);
                current = y + tag + "_p";
            }
            var convInputs = n.Inputs.Count > 2 && n.Inputs[2].Length > 0 ? new[] { current, wName, n.Inputs[2] } : new[] { current, wName };
            WriteNode(sb, "Conv", convInputs, new[] { y }, n.Name + tag + "_Conv", new (string, long[])[]
            {
                ("dilations", new long[] { 1 }), ("group", new[] { group }), ("kernel_shape", new[] { k }), ("pads", new long[] { 0, 0 }), ("strides", new long[] { 1 }),
            });
            replace[index] = sb.ToArray();
            if (uses.GetValueOrDefault(n.Inputs[1]) == 1) replace[w.Index] = Array.Empty<byte>(); // the old kernel isn't needed
            rewritten++;
        }
        if (rewritten == 0) return model;

        var graph = new MemoryStream(graphField.Length + (int)added.Length + 4096);
        for (int i = 0; i < graphFields.Count; i++)
        {
            if (replace.TryGetValue(i, out var bytes)) graph.Write(bytes);
            else graph.Write(model, graphFields[i].Start, graphFields[i].End - graphFields[i].Start);
        }
        added.Position = 0;
        added.CopyTo(graph);

        var result = new MemoryStream(model.Length + (int)added.Length);
        foreach (var f in top)
        {
            if (f.Number == 7 && f.WireType == 2 && f.Start == graphField.Start)
            {
                WriteTag(result, 7, 2);
                WriteVarint(result, (ulong)graph.Length);
                graph.Position = 0;
                graph.CopyTo(result);
            }
            else result.Write(model, f.Start, f.End - f.Start);
        }
        return result.ToArray();
    }

    // ------------------------------------------------------------------ reading

    private static List<Field> ReadFields(byte[] data, int start, int end)
    {
        var fields = new List<Field>();
        int pos = start;
        while (pos < end)
        {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos);
            int number = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    ulong v = ReadVarint(data, ref pos);
                    fields.Add(new Field(number, wire, fieldStart, pos, 0, pos, v));
                    break;
                case 1:
                    fields.Add(new Field(number, wire, fieldStart, pos, 8, pos + 8, 0));
                    pos += 8;
                    break;
                case 2:
                    int len = checked((int)ReadVarint(data, ref pos));
                    fields.Add(new Field(number, wire, fieldStart, pos, len, pos + len, 0));
                    pos += len;
                    break;
                case 5:
                    fields.Add(new Field(number, wire, fieldStart, pos, 4, pos + 4, 0));
                    pos += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wire} in the model file.");
            }
            if (pos > end) throw new InvalidDataException("The model file is truncated.");
        }
        return fields;
    }

    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Bad varint in the model file.");
    }

    private static string Str(byte[] data, Field f) => Encoding.UTF8.GetString(data, f.ValueStart, f.Length);

    private static List<long> Varints(byte[] data, Field f)
    {
        var values = new List<long>();
        if (f.WireType == 0) values.Add((long)f.Varint);
        else if (f.WireType == 2)
            for (int p = f.ValueStart; p < f.End;) values.Add((long)ReadVarint(data, ref p));
        return values;
    }

    private static Node ParseNode(byte[] data, Field nodeField)
    {
        var node = new Node();
        foreach (var f in ReadFields(data, nodeField.ValueStart, nodeField.End))
        {
            switch (f.Number)
            {
                case 1 when f.WireType == 2: node.Inputs.Add(Str(data, f)); break;
                case 2 when f.WireType == 2: node.Outputs.Add(Str(data, f)); break;
                case 3 when f.WireType == 2: node.Name = Str(data, f); break;
                case 4 when f.WireType == 2: node.OpType = Str(data, f); break;
                case 7 when f.WireType == 2: node.Domain = Str(data, f); break;
                case 5 when f.WireType == 2:
                    // AttributeProto: name = 1, i = 3, ints = 8.
                    string? name = null;
                    var ints = new List<long>();
                    bool numeric = false;
                    foreach (var a in ReadFields(data, f.ValueStart, f.End))
                    {
                        if (a.Number == 1 && a.WireType == 2) name = Str(data, a);
                        else if (a.Number == 3 && a.WireType == 0) { ints.Add((long)a.Varint); numeric = true; }
                        else if (a.Number == 8) { ints.AddRange(Varints(data, a)); numeric = true; }
                    }
                    if (name is null) break;
                    if (numeric) node.Ints[name] = ints.ToArray();
                    else node.OtherAttributes.Add(name);
                    break;
            }
        }
        return node;
    }

    private static Tensor? ParseTensor(byte[] data, Field tensorField)
    {
        string? name = null;
        var dims = new List<long>();
        int type = 0;
        Field raw = default, floats = default;
        var floatList = new List<float>();
        foreach (var f in ReadFields(data, tensorField.ValueStart, tensorField.End))
        {
            switch (f.Number)
            {
                case 1: dims.AddRange(Varints(data, f)); break;
                case 2 when f.WireType == 0: type = (int)f.Varint; break;
                case 4 when f.WireType == 2: floats = f; break;
                case 4 when f.WireType == 5: floatList.Add(BitConverter.ToSingle(data, f.ValueStart)); break;
                case 8 when f.WireType == 2: name = Str(data, f); break;
                case 9 when f.WireType == 2: raw = f; break;
            }
        }
        if (name is null) return null;
        float[]? values = null;
        if (type == 1) // FLOAT
        {
            long count = dims.Aggregate(1L, (a, b) => a * b);
            if (raw.Length > 0 && raw.Length == count * 4)
            {
                values = new float[count];
                Buffer.BlockCopy(data, raw.ValueStart, values, 0, raw.Length);
            }
            else if (floats.Length > 0 && floats.Length == count * 4)
            {
                values = new float[count];
                Buffer.BlockCopy(data, floats.ValueStart, values, 0, floats.Length);
            }
            else if (floatList.Count == count) values = floatList.ToArray();
        }
        return new Tensor(name, dims.ToArray(), type, values);
    }

    // ------------------------------------------------------------------ writing

    private static void WriteTag(Stream s, int number, int wire) => WriteVarint(s, (ulong)((number << 3) | wire));

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    private static void WriteBytes(Stream s, int number, byte[] bytes)
    {
        WriteTag(s, number, 2);
        WriteVarint(s, (ulong)bytes.Length);
        s.Write(bytes);
    }

    private static void WriteString(Stream s, int number, string value) => WriteBytes(s, number, Encoding.UTF8.GetBytes(value));

    /// <summary>A NodeProto (GraphProto field 1) with INTS attributes.</summary>
    private static void WriteNode(Stream graph, string opType, string[] inputs, string[] outputs, string name, (string Name, long[] Values)[]? ints)
    {
        var node = new MemoryStream();
        foreach (var i in inputs) WriteString(node, 1, i);
        foreach (var o in outputs) WriteString(node, 2, o);
        WriteString(node, 3, name);
        WriteString(node, 4, opType);
        foreach (var (attrName, values) in ints ?? Array.Empty<(string, long[])>())
        {
            var attr = new MemoryStream();
            WriteString(attr, 1, attrName);
            bool single = attrName == "group";
            if (single)
            {
                WriteTag(attr, 3, 0); // i
                WriteVarint(attr, (ulong)values[0]);
                WriteTag(attr, 20, 0);
                WriteVarint(attr, 2); // type = INT
            }
            else
            {
                foreach (var v in values)
                {
                    WriteTag(attr, 8, 0); // ints
                    WriteVarint(attr, (ulong)v);
                }
                WriteTag(attr, 20, 0);
                WriteVarint(attr, 7); // type = INTS
            }
            WriteBytes(node, 5, attr.ToArray());
        }
        WriteBytes(graph, 1, node.ToArray());
    }

    /// <summary>An initializer (GraphProto field 5): FLOAT with raw data.</summary>
    private static void WriteFloatTensor(Stream graph, string name, long[] dims, float[] values)
    {
        var t = new MemoryStream();
        foreach (var d in dims) { WriteTag(t, 1, 0); WriteVarint(t, (ulong)d); }
        WriteTag(t, 2, 0);
        WriteVarint(t, 1);
        WriteString(t, 8, name);
        var raw = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        WriteBytes(t, 9, raw);
        WriteBytes(graph, 5, t.ToArray());
    }

    /// <summary>An initializer (GraphProto field 5): INT64 with raw data.</summary>
    private static void WriteInt64Tensor(Stream graph, string name, long[] dims, long[] values)
    {
        var t = new MemoryStream();
        foreach (var d in dims) { WriteTag(t, 1, 0); WriteVarint(t, (ulong)d); }
        WriteTag(t, 2, 0);
        WriteVarint(t, 7);
        WriteString(t, 8, name);
        var raw = new byte[values.Length * 8];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        WriteBytes(t, 9, raw);
        WriteBytes(graph, 5, t.ToArray());
    }
}
