using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace XisfLib.Core.Tests;

// EXPECTED-RED (each pins a gap in Journal/20260919.xisf-1.0-revision1-conformance-assessment.md):
//   Int8_ScalarRoundTrips            — §A item 2 / §B "all scalar widths" (SerializeProperty throws)
//   UInt8_ScalarRoundTrips           — §A item 2 / §B
//   Int16_ScalarRoundTrips           — §A item 2 / §B
//   UInt16_ScalarRoundTrips          — §A item 2 / §B
//   Int64_ScalarRoundTrips           — §A item 2 / §B
//   UInt64_ScalarRoundTrips          — §A item 2 / §B
//   ScalarWidth_ParsesFromCraftedHeader          — §A item 2 (read side; misparses as String today)
//   ScalarAlternateTypeName_ParsesToTypedScalar  — §8.4.4.1 Table 3 alternate names
//   Boolean_ParsesNumericForms                   — §A "Boolean 1/0 must be accepted" (bool.Parse throws)
//   Float32_ParsesNonFiniteForms                 — §A non-finite floats (grammar §8.3.3)
//   Float64_ParsesNonFiniteForms                 — §A non-finite floats
//   Float32_EmitsCanonicalNonFiniteForms         — §A (emits Infinity/-Infinity today)
//   Float64_EmitsCanonicalNonFiniteForms         — §A
//   I8_VectorAndMatrixRoundTrip … F64_VectorAndMatrixRoundTrip (10) — §C item 1 / contract item 6
//   VectorAlternateTypeName_ParsesSameAsPrimary  — §8.4.4.5 Table 7 alternate names
//   MatrixAlternateTypeName_ParsesSameAsPrimary  — §8.4.4.6 Table 8 alternate names
//   EmptyVector_RoundTrips                       — §D "empty vectors = zero-length inline block"
//   EmptyMatrix_RoundTrips                       — §D
//   EmptyVector_ParsesFromZeroLengthInlineBlock  — §D
//   Vector_ReadsFromAttachmentBlock              — §C item 1 (location on Property never parsed)
//   Vector_ReadsFromInlineBlock                  — §A item 4 (inline blocks deserialize empty)
//   Matrix_ReadsRowMajor                         — §8.4.4.6 row order
//   Matrix_WritesRowMajor                        — §8.4.4.6 row order
//   F64Vector_CarriesNonFiniteElements           — contract item 6 + item 3
//
// EXPECTED-GREEN (must not regress):
//   Int32_ScalarRoundTrips, UInt32_ScalarRoundTrips, Float32_ScalarRoundTrips, Float64_ScalarRoundTrips
//   Boolean_RoundTripsAndEmitsWords
//   Boolean_ParsesWordForms
//   Float32_EmitsFiniteWithG9
//   Float64_EmitsFiniteWithG17
//   String_RoundTrips
//   String_ParsesFromCharacterContent
//   TimePoint_RoundTrips
//   TimePoint_ParsesZuluForm
public sealed class PropertyTests
{
    private const string Id = "TestProp";
    private const string VectorId = "TestVector";
    private const string MatrixId = "TestMatrix";

    // ==================== Scalar width matrix — write + round-trip ====================

    [Fact]
    public async Task Int8_ScalarRoundTrips()
    {
        await AssertScalar<sbyte>(sbyte.MinValue, "Int8");
        await AssertScalar<sbyte>(0, "Int8");
        await AssertScalar<sbyte>(sbyte.MaxValue, "Int8");
    }

    [Fact]
    public async Task UInt8_ScalarRoundTrips()
    {
        await AssertScalar<byte>(byte.MinValue, "UInt8");
        await AssertScalar<byte>(0, "UInt8");
        await AssertScalar<byte>(byte.MaxValue, "UInt8");
    }

    [Fact]
    public async Task Int16_ScalarRoundTrips()
    {
        await AssertScalar<short>(short.MinValue, "Int16");
        await AssertScalar<short>(0, "Int16");
        await AssertScalar<short>(short.MaxValue, "Int16");
    }

    [Fact]
    public async Task UInt16_ScalarRoundTrips()
    {
        await AssertScalar<ushort>(ushort.MinValue, "UInt16");
        await AssertScalar<ushort>(0, "UInt16");
        await AssertScalar<ushort>(ushort.MaxValue, "UInt16");
    }

    [Fact]
    public async Task Int32_ScalarRoundTrips()
    {
        await AssertScalar(int.MinValue, "Int32");
        await AssertScalar(0, "Int32");
        await AssertScalar(int.MaxValue, "Int32");
    }

    [Fact]
    public async Task UInt32_ScalarRoundTrips()
    {
        await AssertScalar(uint.MinValue, "UInt32");
        await AssertScalar(0u, "UInt32");
        await AssertScalar(uint.MaxValue, "UInt32");
    }

    [Fact]
    public async Task Int64_ScalarRoundTrips()
    {
        await AssertScalar(long.MinValue, "Int64");
        await AssertScalar(0L, "Int64");
        await AssertScalar(long.MaxValue, "Int64");
    }

    [Fact]
    public async Task UInt64_ScalarRoundTrips()
    {
        await AssertScalar(ulong.MinValue, "UInt64");
        await AssertScalar(0UL, "UInt64");
        await AssertScalar(ulong.MaxValue, "UInt64");
    }

    [Fact]
    public async Task Float32_ScalarRoundTrips()
    {
        await AssertScalar(float.MinValue, "Float32");
        await AssertScalar(0f, "Float32");
        await AssertScalar(float.MaxValue, "Float32");
    }

    [Fact]
    public async Task Float64_ScalarRoundTrips()
    {
        await AssertScalar(double.MinValue, "Float64");
        await AssertScalar(0d, "Float64");
        await AssertScalar(double.MaxValue, "Float64");
    }

    // ==================== Scalar width matrix — read from crafted headers ====================

    [Theory]
    [InlineData("Int8", "-128", typeof(sbyte))]
    [InlineData("Int8", "127", typeof(sbyte))]
    [InlineData("UInt8", "0", typeof(byte))]
    [InlineData("UInt8", "255", typeof(byte))]
    [InlineData("Int16", "-32768", typeof(short))]
    [InlineData("Int16", "32767", typeof(short))]
    [InlineData("UInt16", "0", typeof(ushort))]
    [InlineData("UInt16", "65535", typeof(ushort))]
    [InlineData("Int32", "-2147483648", typeof(int))]
    [InlineData("Int32", "2147483647", typeof(int))]
    [InlineData("UInt32", "0", typeof(uint))]
    [InlineData("UInt32", "4294967295", typeof(uint))]
    [InlineData("Int64", "-9223372036854775808", typeof(long))]
    [InlineData("Int64", "9223372036854775807", typeof(long))]
    [InlineData("UInt64", "0", typeof(ulong))]
    [InlineData("UInt64", "18446744073709551615", typeof(ulong))]
    public async Task ScalarWidth_ParsesFromCraftedHeader(string typeName, string text, Type clrType)
        => await AssertParsedScalar(typeName, text, clrType);

    // §8.4.4.1 Table 3 lists an alternate name for most scalar types; both are legal in a file.
    [Theory]
    [InlineData("Byte", "200", typeof(byte))]
    [InlineData("Short", "-300", typeof(short))]
    [InlineData("UShort", "40000", typeof(ushort))]
    [InlineData("Int", "-70000", typeof(int))]
    [InlineData("UInt", "3000000000", typeof(uint))]
    [InlineData("Float", "1.25", typeof(float))]
    [InlineData("Double", "1.25", typeof(double))]
    public async Task ScalarAlternateTypeName_ParsesToTypedScalar(string typeName, string text, Type clrType)
        => await AssertParsedScalar(typeName, text, clrType);

    // ==================== Boolean ====================

    [Fact]
    public async Task Boolean_RoundTripsAndEmitsWords()
    {
        foreach (var value in new[] { true, false })
        {
            var unit = TestKit.PropertyUnit(new XisfScalarProperty<bool>(Id, value));
            var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), Id)!;

            Assert.Equal("Boolean", (string?)element.Attribute("type"));
            Assert.Equal(value ? "true" : "false", TestKit.PropertyValue(element));

            var back = Assert.IsType<XisfScalarProperty<bool>>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
            Assert.Equal(value, back.Value);
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task Boolean_ParsesWordForms(string text, bool expected)
        => Assert.Equal(expected, await ParseBoolean(text));

    // §8.3.4: decoders shall also accept the integers 1 and 0.
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public async Task Boolean_ParsesNumericForms(string text, bool expected)
        => Assert.Equal(expected, await ParseBoolean(text));

    // ==================== Non-finite floats ====================

    // §8.3.3 grammar: NaN | [-+]Inf | -?nan | -?inf — decoders must accept every form.
    [Theory]
    [InlineData("NaN", float.NaN)]
    [InlineData("nan", float.NaN)]
    [InlineData("-nan", float.NaN)]
    [InlineData("+Inf", float.PositiveInfinity)]
    [InlineData("inf", float.PositiveInfinity)]
    [InlineData("-Inf", float.NegativeInfinity)]
    [InlineData("-inf", float.NegativeInfinity)]
    public async Task Float32_ParsesNonFiniteForms(string text, float expected)
    {
        var property = await ParseProperty("Float32", text);
        var scalar = Assert.IsType<XisfScalarProperty<float>>(property);
        Assert.Equal(expected, scalar.Value);
    }

    [Theory]
    [InlineData("NaN", double.NaN)]
    [InlineData("nan", double.NaN)]
    [InlineData("-nan", double.NaN)]
    [InlineData("+Inf", double.PositiveInfinity)]
    [InlineData("inf", double.PositiveInfinity)]
    [InlineData("-Inf", double.NegativeInfinity)]
    [InlineData("-inf", double.NegativeInfinity)]
    public async Task Float64_ParsesNonFiniteForms(string text, double expected)
    {
        var property = await ParseProperty("Float64", text);
        var scalar = Assert.IsType<XisfScalarProperty<double>>(property);
        Assert.Equal(expected, scalar.Value);
    }

    // §8.3.3: encoders should use the canonical forms NaN / +Inf / -Inf.
    [Theory]
    [InlineData(float.NaN, "NaN")]
    [InlineData(float.PositiveInfinity, "+Inf")]
    [InlineData(float.NegativeInfinity, "-Inf")]
    public async Task Float32_EmitsCanonicalNonFiniteForms(float value, string expected)
    {
        var unit = TestKit.PropertyUnit(new XisfScalarProperty<float>(Id, value));
        var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), Id)!;
        Assert.Equal(expected, TestKit.PropertyValue(element));

        var back = Assert.IsType<XisfScalarProperty<float>>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
        Assert.Equal(value, back.Value);
    }

    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "+Inf")]
    [InlineData(double.NegativeInfinity, "-Inf")]
    public async Task Float64_EmitsCanonicalNonFiniteForms(double value, string expected)
    {
        var unit = TestKit.PropertyUnit(new XisfScalarProperty<double>(Id, value));
        var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), Id)!;
        Assert.Equal(expected, TestKit.PropertyValue(element));

        var back = Assert.IsType<XisfScalarProperty<double>>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
        Assert.Equal(value, back.Value);
    }

    [Fact]
    public async Task Float32_EmitsFiniteWithG9()
    {
        var unit = TestKit.PropertyUnit(new XisfScalarProperty<float>(Id, 1.2345678f));
        var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), Id)!;
        Assert.Equal(1.2345678f.ToString("G9", CultureInfo.InvariantCulture), TestKit.PropertyValue(element));
    }

    [Fact]
    public async Task Float64_EmitsFiniteWithG17()
    {
        var unit = TestKit.PropertyUnit(new XisfScalarProperty<double>(Id, Math.PI));
        var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), Id)!;
        Assert.Equal(Math.PI.ToString("G17", CultureInfo.InvariantCulture), TestKit.PropertyValue(element));
    }

    // ==================== String + TimePoint regressions ====================

    [Fact]
    public async Task String_RoundTrips()
    {
        var unit = TestKit.PropertyUnit(new XisfStringProperty(Id, "The quick brown fox"));
        var back = Assert.IsType<XisfStringProperty>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
        Assert.Equal("The quick brown fox", back.Value);
    }

    // §11.1.6: a String property may serialize its value directly in character data contents.
    [Fact]
    public async Task String_ParsesFromCharacterContent()
    {
        var property = await ReadSingleProperty(
            $"<Property id=\"{Id}\" type=\"String\">Definitely one of my very best images.</Property>");
        var stringProperty = Assert.IsType<XisfStringProperty>(property);
        Assert.Equal("Definitely one of my very best images.", stringProperty.Value);
    }

    [Fact]
    public async Task TimePoint_RoundTrips()
    {
        var value = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 890, TimeSpan.FromHours(-5));
        var unit = TestKit.PropertyUnit(new XisfTimePointProperty(Id, value));
        var back = Assert.IsType<XisfTimePointProperty>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
        Assert.Equal(value, back.Value);
    }

    [Fact]
    public async Task TimePoint_ParsesZuluForm()
    {
        var property = await ParseProperty("TimePoint", "2015-01-23T19:52:31.46Z");
        var timePoint = Assert.IsType<XisfTimePointProperty>(property);
        Assert.Equal(new DateTimeOffset(2015, 1, 23, 19, 52, 31, 460, TimeSpan.Zero), timePoint.Value);
    }

    // ==================== Vector + Matrix, full contract type set ====================
    // Type identifiers are the primary names of §8.4.4.5 Table 7 and §8.4.4.6 Table 8.

    [Fact]
    public Task I8_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new sbyte[] { -128, -1, 0, 1, 42, 127 }, "I8Vector", "I8Matrix");

    [Fact]
    public Task UI8_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new byte[] { 0, 1, 2, 3, 254, 255 }, "UI8Vector", "UI8Matrix");

    [Fact]
    public Task I16_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new short[] { -32768, -1, 0, 1, 4242, 32767 }, "I16Vector", "I16Matrix");

    [Fact]
    public Task UI16_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new ushort[] { 0, 1, 2, 3, 40000, 65535 }, "UI16Vector", "UI16Matrix");

    [Fact]
    public Task I32_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new[] { int.MinValue, -1, 0, 1, 70000, int.MaxValue }, "I32Vector", "I32Matrix");

    [Fact]
    public Task UI32_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new[] { 0u, 1u, 2u, 3u, 3000000000u, uint.MaxValue }, "UI32Vector", "UI32Matrix");

    [Fact]
    public Task I64_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new[] { long.MinValue, -1L, 0L, 1L, 5000000000L, long.MaxValue }, "I64Vector", "I64Matrix");

    [Fact]
    public Task UI64_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new[] { 0UL, 1UL, 2UL, 3UL, 5000000000UL, ulong.MaxValue }, "UI64Vector", "UI64Matrix");

    [Fact]
    public Task F32_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new[] { float.MinValue, -1.5f, 0f, 1.5f, 3.25f, float.MaxValue }, "F32Vector", "F32Matrix");

    [Fact]
    public Task F64_VectorAndMatrixRoundTrip()
        => AssertVectorAndMatrix(new[] { double.MinValue, -1.5, 0d, 1.5, 3.25, double.MaxValue }, "F64Vector", "F64Matrix");

    [Theory]
    [InlineData("ByteArray", typeof(byte), 1)]
    [InlineData("UI8Vector", typeof(byte), 1)]
    [InlineData("IVector", typeof(int), 4)]
    [InlineData("UIVector", typeof(uint), 4)]
    [InlineData("Vector", typeof(double), 8)]
    public async Task VectorAlternateTypeName_ParsesSameAsPrimary(string typeName, Type elementType, int elementSize)
    {
        var block = new byte[elementSize * 4];
        var property = await ReadSingleProperty(
            $"<Property id=\"{VectorId}\" type=\"{typeName}\" length=\"4\" location=\"inline:base64\">{TestKit.Base64(block)}</Property>");

        Assert.Equal(typeof(XisfVectorProperty<>).MakeGenericType(elementType), property.GetType());
    }

    [Theory]
    [InlineData("ByteMatrix", typeof(byte), 1)]
    [InlineData("UI8Matrix", typeof(byte), 1)]
    [InlineData("IMatrix", typeof(int), 4)]
    [InlineData("UIMatrix", typeof(uint), 4)]
    [InlineData("Matrix", typeof(double), 8)]
    public async Task MatrixAlternateTypeName_ParsesSameAsPrimary(string typeName, Type elementType, int elementSize)
    {
        var block = new byte[elementSize * 6];
        var property = await ReadSingleProperty(
            $"<Property id=\"{MatrixId}\" type=\"{typeName}\" rows=\"2\" columns=\"3\" location=\"inline:base64\">{TestKit.Base64(block)}</Property>");

        Assert.Equal(typeof(XisfMatrixProperty<>).MakeGenericType(elementType), property.GetType());
    }

    // §11.1.8: an empty vector is serialized as an inline data block with empty character contents.
    [Fact]
    public async Task EmptyVector_RoundTrips()
    {
        var unit = TestKit.PropertyUnit(new XisfVectorProperty<double>(VectorId, Array.Empty<double>()));
        var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), VectorId)!;

        Assert.Equal("F64Vector", (string?)element.Attribute("type"));
        Assert.Equal("0", (string?)element.Attribute("length"));
        Assert.StartsWith("inline:", (string?)element.Attribute("location"));
        Assert.Equal(string.Empty, element.Value.Trim());

        var back = Assert.IsType<XisfVectorProperty<double>>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
        Assert.Empty(back.Values);
    }

    // §11.1.9: an empty matrix (zero rows or columns) likewise.
    [Fact]
    public async Task EmptyMatrix_RoundTrips()
    {
        var unit = TestKit.PropertyUnit(new XisfMatrixProperty<double>(MatrixId, 0, 0, Array.Empty<double>()));
        var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), MatrixId)!;

        Assert.Equal("F64Matrix", (string?)element.Attribute("type"));
        Assert.Equal("0", (string?)element.Attribute("rows"));
        Assert.Equal("0", (string?)element.Attribute("columns"));
        Assert.Equal(string.Empty, element.Value.Trim());

        var back = Assert.IsType<XisfMatrixProperty<double>>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
        Assert.Empty(back.Values);
        Assert.Equal(0u, back.Rows);
        Assert.Equal(0u, back.Columns);
    }

    [Fact]
    public async Task EmptyVector_ParsesFromZeroLengthInlineBlock()
    {
        var property = await ReadSingleProperty(
            $"<Property id=\"{VectorId}\" type=\"F64Vector\" length=\"0\" location=\"inline:base64\"></Property>");
        var vector = Assert.IsType<XisfVectorProperty<double>>(property);
        Assert.Empty(vector.Values);
    }

    [Fact]
    public async Task Vector_ReadsFromInlineBlock()
    {
        var block = TestKit.Concat(
            BitConverter.GetBytes(1.5d),
            BitConverter.GetBytes(-2.5d),
            BitConverter.GetBytes(3.0d));

        var property = await ReadSingleProperty(
            $"<Property id=\"{VectorId}\" type=\"F64Vector\" length=\"3\" location=\"inline:base64\">{TestKit.Base64(block)}</Property>");

        var vector = Assert.IsType<XisfVectorProperty<double>>(property);
        Assert.Equal(new[] { 1.5d, -2.5d, 3.0d }, vector.Values);
    }

    [Fact]
    public async Task Vector_ReadsFromAttachmentBlock()
    {
        var block = TestKit.Concat(
            BitConverter.GetBytes(1.5d),
            BitConverter.GetBytes(-2.5d),
            BitConverter.GetBytes(3.0d));

        var headerXml = TestKit.HeaderXml(
            $"<Property id=\"{VectorId}\" type=\"F64Vector\" length=\"3\" location=\"attachment:{{POS0}}:{{SIZE0}}\"/>");

        var unit = await TestKit.ReadBytes(TestKit.BuildMonolithic(headerXml, block));
        var vector = Assert.IsType<XisfVectorProperty<double>>(Assert.Single(unit.GlobalProperties));
        Assert.Equal(new[] { 1.5d, -2.5d, 3.0d }, vector.Values);
    }

    // §8.4.4.6: matrix elements are stored in row order — all of row 0 first, by column.
    [Fact]
    public async Task Matrix_ReadsRowMajor()
    {
        var block = TestKit.Concat(new[] { 10, 20, 30, 40, 50, 60 }.Select(v => BitConverter.GetBytes(v)).ToArray());

        var property = await ReadSingleProperty(
            $"<Property id=\"{MatrixId}\" type=\"I32Matrix\" rows=\"2\" columns=\"3\" location=\"inline:base64\">{TestKit.Base64(block)}</Property>");

        var matrix = Assert.IsType<XisfMatrixProperty<int>>(property);
        Assert.Equal(2u, matrix.Rows);
        Assert.Equal(3u, matrix.Columns);
        Assert.Equal(new[] { 10, 20, 30, 40, 50, 60 }, matrix.Values);
        Assert.Equal(40, matrix.Values[1 * 3 + 0]);
    }

    [Fact]
    public async Task Matrix_WritesRowMajor()
    {
        var values = new[] { 10, 20, 30, 40, 50, 60 };
        var unit = TestKit.PropertyUnit(new XisfMatrixProperty<int>(MatrixId, 2, 3, values));
        var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), MatrixId)!;

        Assert.Equal("inline:base64", (string?)element.Attribute("location"));
        var written = Convert.FromBase64String(element.Value.Trim());
        Assert.Equal(TestKit.Concat(values.Select(v => BitConverter.GetBytes(v)).ToArray()), written);
    }

    [Fact]
    public async Task F64Vector_CarriesNonFiniteElements()
    {
        var values = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0d };
        var unit = TestKit.PropertyUnit(new XisfVectorProperty<double>(VectorId, values));
        var back = Assert.IsType<XisfVectorProperty<double>>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
        Assert.Equal(values, back.Values);
    }

    // ==================== Helpers ====================

    private static async Task AssertScalar<T>(T value, string expectedTypeName) where T : struct
    {
        var unit = TestKit.PropertyUnit(new XisfScalarProperty<T>(Id, value));

        var element = TestKit.FindProperty(await TestKit.WrittenHeader(unit), Id)!;
        Assert.Equal(expectedTypeName, (string?)element.Attribute("type"));

        var back = Assert.IsType<XisfScalarProperty<T>>(Assert.Single((await TestKit.RoundTrip(unit)).GlobalProperties));
        Assert.Equal(value, back.Value);
    }

    private static async Task AssertVectorAndMatrix<T>(T[] values, string vectorType, string matrixType)
        where T : struct
    {
        var vectorUnit = TestKit.PropertyUnit(new XisfVectorProperty<T>(VectorId, values));
        var vectorElement = TestKit.FindProperty(await TestKit.WrittenHeader(vectorUnit), VectorId)!;

        Assert.Equal(vectorType, (string?)vectorElement.Attribute("type"));
        Assert.Equal(values.Length.ToString(CultureInfo.InvariantCulture), (string?)vectorElement.Attribute("length"));
        Assert.Null(vectorElement.Attribute("value"));

        var vectorBack = Assert.IsType<XisfVectorProperty<T>>(
            Assert.Single((await TestKit.RoundTrip(vectorUnit)).GlobalProperties));
        Assert.Equal(values, vectorBack.Values);

        var matrixUnit = TestKit.PropertyUnit(new XisfMatrixProperty<T>(MatrixId, 2, 3, values));
        var matrixElement = TestKit.FindProperty(await TestKit.WrittenHeader(matrixUnit), MatrixId)!;

        Assert.Equal(matrixType, (string?)matrixElement.Attribute("type"));
        Assert.Equal("2", (string?)matrixElement.Attribute("rows"));
        Assert.Equal("3", (string?)matrixElement.Attribute("columns"));
        Assert.Null(matrixElement.Attribute("value"));

        var matrixBack = Assert.IsType<XisfMatrixProperty<T>>(
            Assert.Single((await TestKit.RoundTrip(matrixUnit)).GlobalProperties));
        Assert.Equal(2u, matrixBack.Rows);
        Assert.Equal(3u, matrixBack.Columns);
        Assert.Equal(values, matrixBack.Values);
    }

    private static async Task AssertParsedScalar(string typeName, string text, Type clrType)
    {
        var property = await ParseProperty(typeName, text);

        Assert.Equal(typeof(XisfScalarProperty<>).MakeGenericType(clrType), property.GetType());

        var actual = property.GetType().GetProperty("Value")!.GetValue(property);
        Assert.Equal(Convert.ChangeType(text, clrType, CultureInfo.InvariantCulture), actual);
    }

    private static async Task<bool> ParseBoolean(string text)
    {
        var property = await ParseProperty("Boolean", text);
        return Assert.IsType<XisfScalarProperty<bool>>(property).Value;
    }

    private static Task<XisfProperty> ParseProperty(string typeName, string text)
        => ReadSingleProperty($"<Property id=\"{Id}\" type=\"{typeName}\" value=\"{text}\"/>");

    private static async Task<XisfProperty> ReadSingleProperty(string propertyXml)
    {
        var unit = await TestKit.ReadBytes(TestKit.BuildMonolithic(TestKit.HeaderXml(propertyXml)));
        return Assert.Single(unit.GlobalProperties);
    }
}
