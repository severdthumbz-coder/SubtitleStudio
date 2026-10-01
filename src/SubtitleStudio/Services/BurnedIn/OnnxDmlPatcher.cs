using System.Text;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// Makes the LaMa model runnable on DirectML, in memory, when it is loaded (the file on disk is not changed).
///
/// The model computes its Fourier transforms with matrix multiplies: a 4D feature map X [N,C,H,W] gets a
/// trailing size-1 axis (Unsqueeze, axis 4) and is multiplied by a 2D DFT matrix A [K,W] built from cos/sin:
///   MatMul(A, Unsqueeze(X, 4))  ->  [N,C,H,K,1]
/// DirectML's matrix multiply takes at most 4 dimensions and rejects this ("The parameter is incorrect").
/// Every such node is rewritten into the identical 4D form
///   Unsqueeze(MatMul(X, Transpose(A)), 4)
/// (out[n,c,h,k] = sum_w A[k,w] X[n,c,h,w] either way). Everything else is copied byte for byte.
///
/// Works directly on the protobuf wire format (ModelProto.graph = 7, GraphProto.node = 1,
/// NodeProto: input = 1, output = 2, name = 3, op_type = 4, attribute = 5, domain = 7), so no ONNX or
/// protobuf library is needed.
/// </summary>
public static class OnnxDmlPatcher
{
    private sealed class Node
    {
        public required byte[] Raw;
        public List<string> Inputs = new();
        public List<string> Outputs = new();
        public string Name = string.Empty;
        public string OpType = string.Empty;
        public string Domain = string.Empty;
        public byte[]? AxesTensor; // Constant nodes: the "value" tensor bytes
    }

    /// <summary>Returns the patched model and how many MatMul nodes were rewritten (0 = returned unchanged).</summary>
    public static byte[] Patch(byte[] model, out int rewritten) => Patch(model, out rewritten, out _);

    /// <summary>
    /// As Patch; <paramref name="flexible"/>: inputs and output whose height and width were made flexible
    /// (none when <paramref name="flexibleSize"/> is false: only the DirectML matrix multiplies are rewritten).
    /// </summary>
    public static byte[] Patch(byte[] model, out int rewritten, out int flexible, bool flexibleSize = true)
    {
        rewritten = 0;
        flexible = 0;
        // ModelProto: find the graph (field 7).
        var top = ReadFields(model, 0, model.Length);
        var graphField = top.FirstOrDefault(f => f.Number == 7 && f.WireType == 2);
        if (graphField.Length == 0) return model;

        var graphFields = ReadFields(model, graphField.ValueStart, graphField.ValueStart + graphField.Length);
        var nodes = new List<(int FieldIndex, Node Node)>();
        var initializers = new Dictionary<string, byte[]>();
        for (int i = 0; i < graphFields.Count; i++)
        {
            var f = graphFields[i];
            if (f.Number == 1 && f.WireType == 2) nodes.Add((i, ParseNode(model, f)));
            else if (f.Number == 5 && f.WireType == 2)
            {
                var tensor = model.AsSpan(f.ValueStart, f.Length).ToArray();
                if (TensorName(tensor) is { } n) initializers[n] = tensor;
            }
        }

        var producer = new Dictionary<string, Node>();
        foreach (var (_, n) in nodes)
            foreach (var o in n.Outputs) producer[o] = n;

        bool IsAxisFour(string name)
        {
            byte[]? tensor = initializers.TryGetValue(name, out var t) ? t : producer.TryGetValue(name, out var p) && p.OpType == "Constant" ? p.AxesTensor : null;
            return tensor is not null && Int64Values(tensor) is [var v] && (v == 4 || v == -1);
        }

        // Which MatMul nodes to rewrite: MatMul(A from Cos/Sin, Unsqueeze(X, axis 4)).
        var replace = new Dictionary<int, byte[]>();
        int counter = 0;
        foreach (var (index, n) in nodes)
        {
            if (n.OpType != "MatMul" || n.Domain.Length > 0 || n.Inputs.Count != 2 || n.Outputs.Count != 1) continue;
            if (!producer.TryGetValue(n.Inputs[0], out var a) || a.OpType is not ("Cos" or "Sin")) continue;
            if (!producer.TryGetValue(n.Inputs[1], out var u) || u.OpType != "Unsqueeze" || u.Inputs.Count != 2 || !IsAxisFour(u.Inputs[1])) continue;

            string x = u.Inputs[0], axes = u.Inputs[1], output = n.Outputs[0];
            string tag = $"__dml4d_{counter++}";
            string aT = n.Inputs[0] + tag + "_At", y = output + tag + "_4d";
            var sb = new MemoryStream();
            WriteNode(sb, "Transpose", new[] { n.Inputs[0] }, new[] { aT }, n.Name + tag + "_T", perm: new long[] { 1, 0 });
            WriteNode(sb, "MatMul", new[] { x, aT }, new[] { y }, n.Name + tag + "_MM", perm: null);
            WriteNode(sb, "Unsqueeze", new[] { y, axes }, new[] { output }, n.Name + tag + "_U", perm: null);
            replace[index] = sb.ToArray();
            rewritten++;
        }
        // Height and width of the inputs and output (GraphProto.input = 11, output = 12): declared fixed at
        // 512, but the model derives everything from the actual size, so any multiple of 64 works. Made
        // flexible, so a 256-pixel patch runs at 256 instead of being enlarged to 512 (a quarter of the work).
        for (int i = 0; i < graphFields.Count; i++)
        {
            var f = graphFields[i];
            if (!flexibleSize || f.WireType != 2 || f.Number is not (11 or 12)) continue;
            if (FlexibleSize(model, f) is { } shapeless) replace[i] = shapeless;
        }
        flexible = replace.Count - rewritten;

        // The shapes recorded for intermediate steps (GraphProto.value_info = 13) are optional hints, all
        // worked out for 512 x 512. The processor ignores them, but DirectML checks each step's result
        // against them ("The parameter is incorrect" on the first step at 256). With a flexible size they
        // are dropped; ONNX Runtime works every shape out from the inputs.
        if (flexible > 0)
            for (int i = 0; i < graphFields.Count; i++)
                if (graphFields[i].Number == 13 && graphFields[i].WireType == 2) replace[i] = Array.Empty<byte>();
        if (replace.Count == 0) return model;

        // Re-emit the graph: rewritten nodes expand in place (keeps the topological order).
        var graph = new MemoryStream();
        for (int i = 0; i < graphFields.Count; i++)
        {
            if (replace.TryGetValue(i, out var bytes)) graph.Write(bytes);
            else graph.Write(model, graphFields[i].Start, graphFields[i].End - graphFields[i].Start);
        }

        var result = new MemoryStream(model.Length + 64 * 1024);
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

    /// <summary>
    /// A ValueInfoProto (graph input or output) with a 4D tensor shape [n, c, H, W] whose H and W are fixed
    /// numbers, re-encoded with H and W as named free dimensions "height" and "width"; null if not such.
    /// ValueInfoProto: name = 1, type = 2. TypeProto: tensor_type = 1. Tensor: elem_type = 1, shape = 2.
    /// TensorShapeProto: dim = 1. Dimension: dim_value = 1, dim_param = 2.
    /// </summary>
    private static byte[]? FlexibleSize(byte[] data, Field valueInfo)
    {
        var vi = ReadFields(data, valueInfo.ValueStart, valueInfo.End);
        var type = vi.FirstOrDefault(v => v.Number == 2 && v.WireType == 2);
        if (type.Length == 0) return null;
        var tt = ReadFields(data, type.ValueStart, type.End);
        var tensor = tt.FirstOrDefault(v => v.Number == 1 && v.WireType == 2);
        if (tensor.Length == 0) return null;
        var tf = ReadFields(data, tensor.ValueStart, tensor.End);
        var shape = tf.FirstOrDefault(v => v.Number == 2 && v.WireType == 2);
        if (shape.Length == 0) return null;
        var dims = ReadFields(data, shape.ValueStart, shape.End).Where(d => d.Number == 1 && d.WireType == 2).ToList();
        if (dims.Count != 4) return null;
        bool Fixed(Field d) => ReadFields(data, d.ValueStart, d.End).Any(x => x.Number == 1 && x.WireType == 0);
        if (!Fixed(dims[2]) || !Fixed(dims[3])) return null;

        // Rebuild inside out; everything else copied as is.
        var newShape = new MemoryStream();
        for (int k = 0; k < 4; k++)
        {
            if (k < 2) { newShape.Write(data, dims[k].Start, dims[k].End - dims[k].Start); continue; }
            var dim = new MemoryStream();
            WriteString(dim, 2, k == 2 ? "height" : "width");
            WriteBytes(newShape, 1, dim.ToArray());
        }
        var newTensor = new MemoryStream();
        foreach (var x in tf)
        {
            if (x.Number == 2 && x.WireType == 2) WriteBytes(newTensor, 2, newShape.ToArray());
            else newTensor.Write(data, x.Start, x.End - x.Start);
        }
        var newType = new MemoryStream();
        foreach (var x in tt)
        {
            if (x.Number == 1 && x.WireType == 2) WriteBytes(newType, 1, newTensor.ToArray());
            else newType.Write(data, x.Start, x.End - x.Start);
        }
        var newVi = new MemoryStream();
        foreach (var x in vi)
        {
            if (x.Number == 2 && x.WireType == 2) WriteBytes(newVi, 2, newType.ToArray());
            else newVi.Write(data, x.Start, x.End - x.Start);
        }
        var result = new MemoryStream();
        WriteBytes(result, valueInfo.Number, newVi.ToArray());
        return result.ToArray();
    }

    // ------------------------------------------------------------------ protobuf reading

    private readonly record struct Field(int Number, int WireType, int Start, int ValueStart, int Length, int End, ulong Varint);

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

    private static Node ParseNode(byte[] data, Field nodeField)
    {
        var node = new Node { Raw = data.AsSpan(nodeField.Start, nodeField.End - nodeField.Start).ToArray() };
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
                    // AttributeProto: name = 1, t (TensorProto) = 5. Keep "value" of Constant nodes.
                    var attr = ReadFields(data, f.ValueStart, f.End);
                    var name = attr.FirstOrDefault(a => a.Number == 1 && a.WireType == 2);
                    var t = attr.FirstOrDefault(a => a.Number == 5 && a.WireType == 2);
                    if (name.Length > 0 && Str(data, name) == "value" && t.Length > 0)
                        node.AxesTensor = data.AsSpan(t.ValueStart, t.Length).ToArray();
                    break;
            }
        }
        return node;
    }

    /// <summary>TensorProto.name (field 8).</summary>
    private static string? TensorName(byte[] tensor)
    {
        foreach (var f in ReadFields(tensor, 0, tensor.Length))
            if (f.Number == 8 && f.WireType == 2) return Str(tensor, f);
        return null;
    }

    /// <summary>INT64 tensor values: int64_data (field 7, packed or not) or raw_data (field 9). Null for other types.</summary>
    private static long[]? Int64Values(byte[] tensor)
    {
        var fields = ReadFields(tensor, 0, tensor.Length);
        var type = fields.FirstOrDefault(f => f.Number == 2 && f.WireType == 0);
        if (type.End != 0 && type.Varint != 7) return null; // 7 = INT64
        var values = new List<long>();
        foreach (var f in fields)
        {
            if (f.Number == 7 && f.WireType == 0) values.Add((long)f.Varint);
            else if (f.Number == 7 && f.WireType == 2)
            {
                int p = f.ValueStart;
                while (p < f.End) values.Add((long)ReadVarint(tensor, ref p));
            }
            else if (f.Number == 9 && f.WireType == 2)
                for (int p = f.ValueStart; p + 8 <= f.End; p += 8) values.Add(BitConverter.ToInt64(tensor, p));
        }
        return values.ToArray();
    }

    // ------------------------------------------------------------------ protobuf writing

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

    /// <summary>A NodeProto (as GraphProto field 1), optionally with an INTS attribute "perm".</summary>
    private static void WriteNode(Stream graph, string opType, string[] inputs, string[] outputs, string name, long[]? perm)
    {
        var node = new MemoryStream();
        foreach (var i in inputs) WriteString(node, 1, i);
        foreach (var o in outputs) WriteString(node, 2, o);
        WriteString(node, 3, name);
        WriteString(node, 4, opType);
        if (perm is not null)
        {
            var attr = new MemoryStream();
            WriteString(attr, 1, "perm");
            foreach (var p in perm)
            {
                WriteTag(attr, 8, 0); // ints (unpacked)
                WriteVarint(attr, (ulong)p);
            }
            WriteTag(attr, 20, 0); // type = INTS
            WriteVarint(attr, 7);
            WriteBytes(node, 5, attr.ToArray());
        }
        WriteBytes(graph, 1, node.ToArray());
    }
}
