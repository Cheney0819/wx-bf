namespace Wx411.Core;

public enum CallpointRegisterSemantics
{
    Sqlite3KeySink,
    KeyInR8LengthInR9D,
    KeyInR9LengthStack5,
    BusinessKeyDecoded,
    BusinessKeyPreEncode,
}

public enum CodecHolderStrategy
{
    LegacyXor,
    StructureOnly,
}

public sealed record CallpointDefinition(
    string Name,
    int SignatureRva,
    int BreakpointRva,
    byte[] ExpectedSig,
    CallpointRegisterSemantics Semantics,
    string Description)
{
    public int Rva => BreakpointRva;
    public int SigLength => ExpectedSig.Length;
}

public sealed record ModuleCallpointProfile(
    string Name,
    string ModuleVersion,
    string ModuleSha256,
    CodecHolderStrategy HolderStrategy,
    IReadOnlyList<CallpointDefinition> Callpoints);

public sealed class UnsupportedModuleException : InvalidOperationException
{
    public const string StableCode = "unsupported_module";

    public UnsupportedModuleException(string? detail = null)
        : base(string.IsNullOrWhiteSpace(detail)
            ? StableCode
            : detail.StartsWith(StableCode, StringComparison.OrdinalIgnoreCase)
                ? detail
                : $"{StableCode}: {detail}")
    {
    }

    public string Code => StableCode;
}

public static class CallpointProfiles
{
    public const int MaxBreakpointsPerAttach = 4;

    public const string TargetModuleVersion = "4.1.11.55";
    public const string TargetModuleSha256 =
        "ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a";

    public const string CurrentModuleVersion = "4.1.12.24";
    public const string CurrentModuleSha256 =
        "f8bb1a54081beb90a6cad36e8951f0eb5d2f3c424d8623558550ba19d35edb01";

    public const string LatestModuleVersion = "4.1.12.26";
    public const string LatestModuleSha256 =
        "4914a621a810ecbc0a132b6ff8f612658cfce323d3989b3e5fe32d4ff343ba46";

    private static readonly byte[] SigSqlite3Key = [
        0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54,
        0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48,
        0x89, 0xD7, 0x48, 0x85, 0xC9, 0x0F, 0x94, 0xC0,
        0x48, 0x85, 0xD2, 0x0F, 0x94, 0xC2, 0x08, 0xC2,
    ];

    private static readonly byte[] SigSqlite3KeyV2 = [
        0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x56, 0x57,
        0x55, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x89,
        0xD6, 0x48, 0x85, 0xC9, 0x0F, 0x94, 0xC0, 0x4D,
        0x85, 0xC0, 0x0F, 0x94, 0xC2, 0x08, 0xC2, 0x45,
    ];

    private static readonly byte[] SigCodecInit = [
        0x41, 0x57, 0x41, 0x56, 0x56, 0x57, 0x55, 0x53,
        0x48, 0x83, 0xEC, 0x38, 0x0F, 0x29, 0x74, 0x24,
        0x20, 0x4C, 0x89, 0xCF, 0x49, 0x89, 0xD6, 0x49,
        0x89, 0xCF, 0x8B, 0x9C, 0x24, 0x90, 0x00, 0x00,
    ];

    public static readonly CallpointDefinition Sqlite3KeySink = new(
        "sqlite3_key_sink",
        SignatureRva: 0x34228B0,
        BreakpointRva: 0x34228BA,
        [0x48, 0x8B, 0x4F, 0x38, 0x48, 0x89, 0xC2, 0x41, 0x89, 0xD8],
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x34228BA: RDX=pKey ptr, R8D=nKey.");

    public static readonly CallpointDefinition Sqlite3KeyEquivalent = new(
        "sqlite3_key_equiv",
        SignatureRva: 0x53D2E70,
        BreakpointRva: 0x53D2E70,
        SigSqlite3Key,
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x53D2E70: RDX=pKey ptr, R8D=nKey.");

    public static readonly CallpointDefinition Sqlite3KeyV2Equivalent = new(
        "sqlite3_key_v2_equiv",
        SignatureRva: 0x53D2F20,
        BreakpointRva: 0x53D2F20,
        SigSqlite3KeyV2,
        CallpointRegisterSemantics.KeyInR8LengthInR9D,
        "0x53D2F20: R8=pKey ptr, R9D=nKey.");

    public static readonly CallpointDefinition CodecAttachEquivalent = new(
        "codec_attach_equiv",
        SignatureRva: 0x53D2C80,
        BreakpointRva: 0x53D2C80,
        [
            0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x56, 0x57,
            0x55, 0x53, 0x48, 0x83, 0xEC, 0x40, 0x48, 0x89,
            0xCE, 0x48, 0x8B, 0x05, 0x68, 0xA1, 0x20, 0x05,
            0x48, 0x31, 0xE0, 0x48, 0x89, 0x44, 0x24, 0x38,
        ],
        CallpointRegisterSemantics.KeyInR8LengthInR9D,
        "0x53D2C80: R8=pKey ptr, R9D=nKey.");

    public static readonly CallpointDefinition CodecInitEquivalent = new(
        "codec_init_equiv",
        SignatureRva: 0x33EEED0,
        BreakpointRva: 0x33EEED0,
        SigCodecInit,
        CallpointRegisterSemantics.KeyInR9LengthStack5,
        "0x33EEED0: R9=pKey ptr, [RSP+0x28]=nKey.");

    public static readonly CallpointDefinition CodecSetPassEquivalent = new(
        "codec_set_pass_equiv",
        SignatureRva: 0x33EE1E0,
        BreakpointRva: 0x33EE1E0,
        [
            0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x56, 0x57,
            0x55, 0x53, 0x48, 0x83, 0xEC, 0x50, 0x44, 0x89,
            0xCF, 0x44, 0x89, 0xC5, 0x49, 0x89, 0xD7, 0x48,
            0x89, 0xCE, 0x48, 0x8B, 0x05, 0xFF, 0xEB, 0x1E,
        ],
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x33EE1E0: RDX=pKey ptr, R8D=nKey.");

    public static readonly CallpointDefinition BusinessKeyDecoded = new(
        "business_key_decoded",
        SignatureRva: 0x338895,
        BreakpointRva: 0x338895,
        [0x48, 0x89, 0xF0],
        CallpointRegisterSemantics.BusinessKeyDecoded,
        "0x338895: RSI=restored std::string.");

    public static readonly CallpointDefinition BusinessKeyPreEncode = new(
        "business_key_pre_encode",
        SignatureRva: 0x3E7DC1,
        BreakpointRva: 0x3E7DC8,
        [0x4C, 0x8B, 0x85, 0xA8, 0x01, 0x00, 0x00],
        CallpointRegisterSemantics.BusinessKeyPreEncode,
        "0x3E7DC8: R8=clear std::string*.");

    private static readonly CallpointDefinition CurrentCodecSetPassEquivalent = new(
        "codec_set_pass_equiv",
        SignatureRva: 0x34824A0,
        BreakpointRva: 0x34824A0,
        [
            0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x56, 0x57,
            0x55, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x44, 0x89,
            0xCF, 0x44, 0x89, 0xC5, 0x49, 0x89, 0xD6, 0x48,
            0x89, 0xCE, 0x31, 0xC0, 0x45, 0x85, 0xC9, 0x0F,
        ],
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x34824A0: RDX=pKey ptr, R8D=nKey before MMV1 transform.");

    private static readonly CallpointDefinition CurrentSqlite3KeySink = new(
        "sqlite3_key_sink",
        SignatureRva: 0x34B5420,
        BreakpointRva: 0x34B542A,
        [0x48, 0x8B, 0x4F, 0x38, 0x48, 0x89, 0xC2, 0x41, 0x89, 0xD8],
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x34B542A: RDX=pKey ptr, R8D=nKey.");

    private static readonly CallpointDefinition CurrentSqlite3KeyEquivalent = new(
        "sqlite3_key_equiv",
        SignatureRva: 0x55341F0,
        BreakpointRva: 0x55341F0,
        SigSqlite3Key,
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x55341F0: RDX=pKey ptr, R8D=nKey.");

    private static readonly CallpointDefinition CurrentSqlite3KeyV2Equivalent = new(
        "sqlite3_key_v2_equiv",
        SignatureRva: 0x55342A0,
        BreakpointRva: 0x55342A0,
        SigSqlite3KeyV2,
        CallpointRegisterSemantics.KeyInR8LengthInR9D,
        "0x55342A0: R8=pKey ptr, R9D=nKey.");

    private static readonly CallpointDefinition CurrentCodecAttachEquivalent = new(
        "codec_attach_equiv",
        SignatureRva: 0x5534000,
        BreakpointRva: 0x5534000,
        [
            0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x56, 0x57,
            0x55, 0x53, 0x48, 0x83, 0xEC, 0x40, 0x48, 0x89,
            0xCE, 0x48, 0x8B, 0x05, 0x28, 0xD3, 0x2B, 0x05,
            0x48, 0x31, 0xE0, 0x48, 0x89, 0x44, 0x24, 0x38,
        ],
        CallpointRegisterSemantics.KeyInR8LengthInR9D,
        "0x5534000: R8=pKey ptr, R9D=nKey.");

    private static readonly CallpointDefinition CurrentCodecInitEquivalent = new(
        "codec_init_equiv",
        SignatureRva: 0x3482C30,
        BreakpointRva: 0x3482C30,
        SigCodecInit,
        CallpointRegisterSemantics.KeyInR9LengthStack5,
        "0x3482C30: R9=pKey ptr, [RSP+0x28]=nKey.");

    private static readonly CallpointDefinition CurrentBusinessKeyDecoded = new(
        "business_key_decoded",
        SignatureRva: 0x33B805,
        BreakpointRva: 0x33B805,
        [0x48, 0x89, 0xF0],
        CallpointRegisterSemantics.BusinessKeyDecoded,
        "0x33B805: RSI=restored std::string.");

    private static readonly CallpointDefinition CurrentBusinessKeyPreEncode = new(
        "business_key_pre_encode",
        SignatureRva: 0x3EA73B,
        BreakpointRva: 0x3EA742,
        [0x4C, 0x8B, 0x85, 0x58, 0x01, 0x00, 0x00],
        CallpointRegisterSemantics.BusinessKeyPreEncode,
        "0x3EA742: R8=clear std::string*.");

    private static readonly CallpointDefinition LatestCodecSetPassEquivalent = new(
        "codec_set_pass_equiv",
        SignatureRva: 0x3485AE0,
        BreakpointRva: 0x3485AE0,
        [
            0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x56, 0x57,
            0x55, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x44, 0x89,
            0xCF, 0x44, 0x89, 0xC5, 0x49, 0x89, 0xD6, 0x48,
            0x89, 0xCE, 0x31, 0xC0, 0x45, 0x85, 0xC9, 0x0F,
        ],
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x3485AE0: RDX=pKey ptr, R8D=nKey before MMV1 transform.");

    private static readonly CallpointDefinition LatestSqlite3KeyEquivalent = new(
        "sqlite3_key_equiv",
        SignatureRva: 0x55380B0,
        BreakpointRva: 0x55380B0,
        SigSqlite3Key,
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x55380B0: RDX=pKey ptr, R8D=nKey.");

    private static readonly CallpointDefinition LatestSqlite3KeyV2Equivalent = new(
        "sqlite3_key_v2_equiv",
        SignatureRva: 0x5538160,
        BreakpointRva: 0x5538160,
        SigSqlite3KeyV2,
        CallpointRegisterSemantics.KeyInR8LengthInR9D,
        "0x5538160: R8=pKey ptr, R9D=nKey.");

    private static readonly CallpointDefinition LatestCodecAttachEquivalent = new(
        "codec_attach_equiv",
        SignatureRva: 0x5537EC0,
        BreakpointRva: 0x5537EC0,
        [
            0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x56, 0x57,
            0x55, 0x53, 0x48, 0x83, 0xEC, 0x40, 0x48, 0x89,
            0xCE, 0x48, 0x8B, 0x05, 0x68, 0xD4, 0x2B, 0x05,
            0x48, 0x31, 0xE0, 0x48, 0x89, 0x44, 0x24, 0x38,
        ],
        CallpointRegisterSemantics.KeyInR8LengthInR9D,
        "0x5537EC0: R8=pKey ptr, R9D=nKey.");

    private static readonly CallpointDefinition LatestSqlite3KeySink = new(
        "sqlite3_key_sink",
        SignatureRva: 0x34B8A60,
        BreakpointRva: 0x34B8A6A,
        [0x48, 0x8B, 0x4F, 0x38, 0x48, 0x89, 0xC2, 0x41, 0x89, 0xD8],
        CallpointRegisterSemantics.Sqlite3KeySink,
        "0x34B8A6A: RDX=pKey ptr, R8D=nKey.");

    private static readonly CallpointDefinition LatestCodecInitEquivalent = new(
        "codec_init_equiv",
        SignatureRva: 0x3486270,
        BreakpointRva: 0x3486270,
        SigCodecInit,
        CallpointRegisterSemantics.KeyInR9LengthStack5,
        "0x3486270: R9=pKey ptr, [RSP+0x28]=nKey.");

    public static CallpointDefinition[] All { get; } = [
        Sqlite3KeySink,
        Sqlite3KeyEquivalent,
        Sqlite3KeyV2Equivalent,
        CodecAttachEquivalent,
        CodecInitEquivalent,
        CodecSetPassEquivalent,
        BusinessKeyDecoded,
        BusinessKeyPreEncode,
    ];

    public static ModuleCallpointProfile Weixin411155 { get; } = new(
        "Weixin.dll 4.1.11.55",
        TargetModuleVersion,
        TargetModuleSha256,
        CodecHolderStrategy.LegacyXor,
        All);

    public static ModuleCallpointProfile Weixin411224 { get; } = new(
        "Weixin.dll 4.1.12.24",
        CurrentModuleVersion,
        CurrentModuleSha256,
        CodecHolderStrategy.StructureOnly,
        [
            CurrentCodecSetPassEquivalent,
            CurrentSqlite3KeyEquivalent,
            CurrentSqlite3KeyV2Equivalent,
            CurrentCodecAttachEquivalent,
            CurrentSqlite3KeySink,
            CurrentCodecInitEquivalent,
            CurrentBusinessKeyDecoded,
            CurrentBusinessKeyPreEncode,
        ]);

    public static ModuleCallpointProfile Weixin411226 { get; } = new(
        "Weixin.dll 4.1.12.26",
        LatestModuleVersion,
        LatestModuleSha256,
        CodecHolderStrategy.StructureOnly,
        [
            LatestCodecSetPassEquivalent,
            LatestSqlite3KeyEquivalent,
            LatestSqlite3KeyV2Equivalent,
            LatestCodecAttachEquivalent,
            LatestSqlite3KeySink,
            LatestCodecInitEquivalent,
        ]);

    public static IReadOnlyList<ModuleCallpointProfile> Supported { get; } = [
        Weixin411155,
        Weixin411224,
        Weixin411226,
    ];

    public static ModuleCallpointProfile Preferred => Weixin411226;

    public static ModuleCallpointProfile? FindByIdentity(string moduleVersion, string moduleSha256)
    {
        ArgumentNullException.ThrowIfNull(moduleVersion);
        ArgumentNullException.ThrowIfNull(moduleSha256);
        return Supported.FirstOrDefault(profile =>
            string.Equals(profile.ModuleVersion, moduleVersion, StringComparison.Ordinal) &&
            string.Equals(profile.ModuleSha256, moduleSha256, StringComparison.OrdinalIgnoreCase));
    }
}
