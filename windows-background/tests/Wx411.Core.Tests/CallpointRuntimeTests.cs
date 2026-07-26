using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Wx411.Core.Windows;

namespace Wx411.Core.Tests;

public sealed class CallpointRuntimeTests
{
    [Fact]
    public void ModuleProfileCatalogSelectsBothExactIdentities()
    {
        var profileType = typeof(CallpointProfiles).Assembly.GetType("Wx411.Core.ModuleCallpointProfile");
        Assert.NotNull(profileType);

        var supportedProperty = typeof(CallpointProfiles).GetProperty(
            "Supported",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(supportedProperty);

        var supported = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            supportedProperty!.GetValue(null));
        var profiles = supported.Cast<object>().ToArray();
        Assert.Equal(2, profiles.Length);

        var find = typeof(CallpointProfiles).GetMethod(
            "FindByIdentity",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(find);

        AssertProfile(
            find!.Invoke(null, [
                "4.1.11.55",
                "ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a",
            ]),
            "4.1.11.55");
        AssertProfile(
            find.Invoke(null, [
                "4.1.12.24",
                "F8BB1A54081BEB90A6CAD36E8951F0EB5D2F3C424D8623558550BA19D35EDB01",
            ]),
            "4.1.12.24");
        Assert.Null(find.Invoke(null, ["4.1.12.24", new string('0', 64)]));

        static void AssertProfile(object? profile, string expectedVersion)
        {
            Assert.NotNull(profile);
            Assert.Equal(expectedVersion, profile!.GetType().GetProperty("ModuleVersion")!.GetValue(profile));
        }
    }

    [Fact]
    public void CurrentModuleProfileDefinesPrioritizedExactEightPoints()
    {
        var profile = RequiredProfile("Weixin411224");
        var callpoints = Assert.IsAssignableFrom<IEnumerable<CallpointDefinition>>(
            profile.GetType().GetProperty("Callpoints")!.GetValue(profile)).ToArray();

        Assert.Equal(
            [
                "codec_set_pass_equiv",
                "sqlite3_key_equiv",
                "sqlite3_key_v2_equiv",
                "codec_attach_equiv",
                "sqlite3_key_sink",
                "codec_init_equiv",
                "business_key_decoded",
                "business_key_pre_encode",
            ],
            callpoints.Select(item => item.Name).ToArray());

        AssertCallpoint(callpoints[0], 0x34824A0, 0x34824A0,
            CallpointRegisterSemantics.Sqlite3KeySink,
            "415741564154565755534883EC204489CF4489C54989D64889CE31C04585C90F");
        AssertCallpoint(callpoints[1], 0x55341F0, 0x55341F0,
            CallpointRegisterSemantics.Sqlite3KeySink,
            "41574156415541545657534883EC204889D74885C90F94C04885D20F94C208C2");
        AssertCallpoint(callpoints[2], 0x55342A0, 0x55342A0,
            CallpointRegisterSemantics.KeyInR8LengthInR9D,
            "415741564154565755534883EC204889D64885C90F94C04D85C00F94C208C245");
        AssertCallpoint(callpoints[3], 0x5534000, 0x5534000,
            CallpointRegisterSemantics.KeyInR8LengthInR9D,
            "415741564154565755534883EC404889CE488B0528D32B054831E04889442438");
        AssertCallpoint(callpoints[4], 0x34B5420, 0x34B542A,
            CallpointRegisterSemantics.Sqlite3KeySink,
            "488B4F384889C24189D8");
        AssertCallpoint(callpoints[5], 0x3482C30, 0x3482C30,
            CallpointRegisterSemantics.KeyInR9LengthStack5,
            "41574156565755534883EC380F297424204C89CF4989D64989CF8B9C24900000");
        AssertCallpoint(callpoints[6], 0x33B805, 0x33B805,
            CallpointRegisterSemantics.BusinessKeyDecoded,
            "4889F0");
        AssertCallpoint(callpoints[7], 0x3EA73B, 0x3EA742,
            CallpointRegisterSemantics.BusinessKeyPreEncode,
            "4C8B8558010000");

        static void AssertCallpoint(
            CallpointDefinition actual,
            int signatureRva,
            int breakpointRva,
            CallpointRegisterSemantics semantics,
            string signatureHex)
        {
            Assert.Equal(signatureRva, actual.SignatureRva);
            Assert.Equal(breakpointRva, actual.BreakpointRva);
            Assert.Equal(semantics, actual.Semantics);
            Assert.Equal(signatureHex, Convert.ToHexString(actual.ExpectedSig));
        }
    }

    [Fact]
    public void HolderStrategiesSeparateLegacyDecodeFromCurrentStructureOnly()
    {
        var legacy = RequiredProfile("Weixin411155");
        var current = RequiredProfile("Weixin411224");
        var strategy = legacy.GetType().GetProperty("HolderStrategy");

        Assert.NotNull(strategy);
        Assert.Equal("LegacyXor", strategy!.GetValue(legacy)!.ToString());
        Assert.Equal("StructureOnly", strategy.GetValue(current)!.ToString());
    }

    [Fact]
    public void ModuleIdentityValidationReturnsTheExactSelectedProfile()
    {
        var validate = typeof(PeCallpointLocator).GetMethod(
            "ValidateIdentity",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(validate);

        var accepted = validate!.Invoke(null, [
            "4.1.12.24",
            "f8bb1a54081beb90a6cad36e8951f0eb5d2f3c424d8623558550ba19d35edb01",
        ]);
        Assert.NotNull(accepted);
        Assert.True((bool)accepted!.GetType().GetProperty("IsValid")!.GetValue(accepted)!);
        var selected = accepted.GetType().GetProperty("Profile")!.GetValue(accepted);
        Assert.Same(RequiredProfile("Weixin411224"), selected);

        var rejected = validate.Invoke(null, ["4.1.12.24", new string('0', 64)]);
        Assert.NotNull(rejected);
        Assert.False((bool)rejected!.GetType().GetProperty("IsValid")!.GetValue(rejected)!);
        Assert.Null(rejected.GetType().GetProperty("Profile")!.GetValue(rejected));
    }

    [Fact]
    public void CallpointsSeparateSignatureAndBreakpointRvas()
    {
        var signatureRva = typeof(CallpointDefinition).GetProperty("SignatureRva");
        var breakpointRva = typeof(CallpointDefinition).GetProperty("BreakpointRva");
        Assert.NotNull(signatureRva);
        Assert.NotNull(breakpointRva);

        AssertCallpoint(CallpointProfiles.Sqlite3KeySink, 0x34228B0, 0x34228BA);
        AssertCallpoint(CallpointProfiles.BusinessKeyDecoded, 0x338895, 0x338895);
        AssertCallpoint(CallpointProfiles.BusinessKeyPreEncode, 0x3E7DC1, 0x3E7DC8);

        void AssertCallpoint(CallpointDefinition definition, int expectedSignature, int expectedBreakpoint)
        {
            Assert.Equal(expectedSignature, (int)signatureRva!.GetValue(definition)!);
            Assert.Equal(expectedBreakpoint, (int)breakpointRva!.GetValue(definition)!);
        }
    }

    [Fact]
    public void CallpointProfileIncludesLowLevelSqlcipherFallbacks()
    {
        Assert.Equal(
            [
                "sqlite3_key_sink",
                "sqlite3_key_equiv",
                "sqlite3_key_v2_equiv",
                "codec_attach_equiv",
                "codec_init_equiv",
                "codec_set_pass_equiv",
                "business_key_decoded",
                "business_key_pre_encode",
            ],
            CallpointProfiles.All.Select(item => item.Name).ToArray());

        AssertCallpoint(
            CallpointProfiles.Sqlite3KeyEquivalent,
            0x53D2E70,
            0x53D2E70,
            CallpointRegisterSemantics.Sqlite3KeySink);
        AssertCallpoint(
            CallpointProfiles.Sqlite3KeyV2Equivalent,
            0x53D2F20,
            0x53D2F20,
            CallpointRegisterSemantics.KeyInR8LengthInR9D);
        AssertCallpoint(
            CallpointProfiles.CodecAttachEquivalent,
            0x53D2C80,
            0x53D2C80,
            CallpointRegisterSemantics.KeyInR8LengthInR9D);
        AssertCallpoint(
            CallpointProfiles.CodecInitEquivalent,
            0x33EEED0,
            0x33EEED0,
            CallpointRegisterSemantics.KeyInR9LengthStack5);
        AssertCallpoint(
            CallpointProfiles.CodecSetPassEquivalent,
            0x33EE1E0,
            0x33EE1E0,
            CallpointRegisterSemantics.Sqlite3KeySink);

        static void AssertCallpoint(
            CallpointDefinition definition,
            int expectedSignature,
            int expectedBreakpoint,
            CallpointRegisterSemantics expectedSemantics)
        {
            Assert.Equal(expectedSignature, definition.SignatureRva);
            Assert.Equal(expectedBreakpoint, definition.BreakpointRva);
            Assert.Equal(expectedSemantics, definition.Semantics);
            Assert.True(definition.ExpectedSig.Length >= 16);
        }
    }

    [Fact]
    public void FixedSampleIdentityIsPartOfCallpointProfile()
    {
        var version = typeof(CallpointProfiles).GetField(
            "TargetModuleVersion",
            BindingFlags.Public | BindingFlags.Static);
        var sha256 = typeof(CallpointProfiles).GetField(
            "TargetModuleSha256",
            BindingFlags.Public | BindingFlags.Static);

        Assert.Equal("4.1.11.55", version?.GetValue(null));
        Assert.Equal(
            "ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a",
            sha256?.GetValue(null));
    }

    [Fact]
    public void ContextAmd64MatchesWindowsX64Abi()
    {
        Assert.Equal(0x4D0, Marshal.SizeOf<ContextAmd64>());
        Assert.Equal(new IntPtr(0x30), Marshal.OffsetOf<ContextAmd64>(nameof(ContextAmd64.ContextFlags)));
        Assert.Equal(new IntPtr(0x88), Marshal.OffsetOf<ContextAmd64>(nameof(ContextAmd64.Rdx)));
        Assert.Equal(new IntPtr(0xB8), Marshal.OffsetOf<ContextAmd64>(nameof(ContextAmd64.R8)));
        Assert.Equal(new IntPtr(0xF8), Marshal.OffsetOf<ContextAmd64>(nameof(ContextAmd64.Rip)));
    }

    [Fact]
    public void DebugEventMatchesWindowsX64Abi()
    {
        Assert.Equal(0xB0, Marshal.SizeOf<DebugEvent>());
        Assert.Equal(new IntPtr(0x10), Marshal.OffsetOf<DebugEvent>("u"));

        var union = typeof(DebugEvent).GetField("u")!.FieldType;
        Assert.NotNull(union.GetField("Exception"));
        Assert.Equal(0xA0, Marshal.SizeOf(union));
    }

    [Fact]
    public void ContextAndThreadFlagsRequestRequiredRegisterAccess()
    {
        Assert.Equal(0x00100001u, NativeMethods.CONTEXT_CONTROL);
        Assert.Equal(0x00100002u, NativeMethods.CONTEXT_INTEGER);
        Assert.Equal(0x0008u, Constant("THREAD_GET_CONTEXT"));
        Assert.Equal(0x0010u, Constant("THREAD_SET_CONTEXT"));

        static uint Constant(string name) =>
            (uint)typeof(NativeMethods)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedAsciiKeyBytesAreParsedWithoutHexEncodingAgain(bool includeSalt)
    {
        var rawKey = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var salt = Enumerable.Range(32, 16).Select(index => (byte)index).ToArray();
        var payload = Convert.ToHexString(rawKey) +
            (includeSalt ? Convert.ToHexString(salt) : string.Empty);
        var encoded = Encoding.ASCII.GetBytes($"x'{payload}'");

        var parse = typeof(SqlCipher4).GetMethod(
            "ParseKeyBytes",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(byte[]), typeof(byte[])],
            modifiers: null);
        Assert.NotNull(parse);

        var actual = (byte[])parse!.Invoke(null, [encoded, salt])!;
        try
        {
            Assert.Equal(rawKey, actual);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(actual);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(rawKey);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(salt);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded);
        }
    }

    [Fact]
    public void CapturedKeyMaterialOwnsClearableBytesInsteadOfImmutableKeyText()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(CapturedKeyMaterial)));
        Assert.Null(typeof(CapturedKeyMaterial).GetProperty("KeyText"));
    }

    private static object RequiredProfile(string propertyName)
    {
        var profileType = typeof(CallpointProfiles).Assembly.GetType("Wx411.Core.ModuleCallpointProfile");
        Assert.NotNull(profileType);
        var property = typeof(CallpointProfiles).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(property);
        var value = property!.GetValue(null);
        Assert.NotNull(value);
        Assert.Equal(profileType, value!.GetType());
        return value;
    }
}
