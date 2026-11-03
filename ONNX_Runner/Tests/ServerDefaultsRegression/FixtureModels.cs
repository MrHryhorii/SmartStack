using System.Text;

// Original CPU-only ONNX fixtures echo inference controls into PCM; they do not model speech quality.
internal static class FixtureModels
{
    internal static byte[] Piper() => Model("output", Value("output", 1, 3),
        [Node("Identity", ["scales"], "output")],
        [Value("input", 7, 1, "N"), Value("input_lengths", 7, 1), Value("scales", 1, 3)]);

    internal static byte[] Color() => Model("converted_audio", Value("converted_audio", 1, 1, 3),
        [Node("Flatten", ["dest_tone"], "tone"), Node("Flatten", ["tau"], "temperature", Attribute("axis", 0)),
            Node("Concat", ["tone", "temperature"], "converted_audio", Attribute("axis", 1))],
        [Value("audio", 1, 1, 3, "N"), Value("audio_length", 7, 1), Value("src_tone", 1, 1, 2, 1),
            Value("dest_tone", 1, 1, 2, 1), Value("tau", 1, 1)]);

    private static byte[] Model(string name, byte[] output, byte[][] nodes, byte[][] inputs)
    {
        byte[] graph = Join(nodes.Select(node => Bytes(1, node)).Concat([Text(2, "server_defaults_" + name)])
            .Concat(inputs.Select(input => Bytes(11, input))).Concat([Bytes(12, output)]).ToArray());
        return Join(Integer(1, 8), Bytes(7, graph), Bytes(8, Integer(2, 13)));
    }

    private static byte[] Node(string operation, string[] inputs, string output, params byte[][] attributes)
        => Join(inputs.Select(input => Text(1, input)).Concat([Text(2, output), Text(4, operation)])
            .Concat(attributes.Select(attribute => Bytes(5, attribute))).ToArray());
    private static byte[] Attribute(string name, int value) => Join(Text(1, name), Integer(3, value), Integer(20, 2));
    private static byte[] Value(string name, int elementType, params object[] dimensions)
    {
        byte[] shape = Join(dimensions.Select(dimension => Bytes(1, dimension is int size ? Integer(1, size) : Text(2, (string)dimension))).ToArray());
        return Join(Text(1, name), Bytes(2, Bytes(1, Join(Integer(1, elementType), Bytes(2, shape)))));
    }
    private static byte[] Text(int field, string text) => Bytes(field, Encoding.UTF8.GetBytes(text));
    private static byte[] Bytes(int field, byte[] value) => Join(Varint((ulong)((field << 3) | 2)), Varint((ulong)value.Length), value);
    private static byte[] Integer(int field, int value) => Join(Varint((ulong)(field << 3)), Varint((ulong)value));
    private static byte[] Varint(ulong value)
    {
        using var stream = new MemoryStream();
        while (value > 127) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
        stream.WriteByte((byte)value);
        return stream.ToArray();
    }
    private static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (byte[] part in parts) stream.Write(part);
        return stream.ToArray();
    }
}
