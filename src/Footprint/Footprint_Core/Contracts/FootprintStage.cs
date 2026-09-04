namespace Footprint.Core.Contracts;

public enum FootprintStage
{
    Footprint_Bootstrap = 1,
    Footprint_Runtime,
    Footprint_WeixinDetection,
    Footprint_VersionVerification,
    Footprint_KeyValidation,
    Footprint_KeyCapture,
    Footprint_WeixinRestart,
    Footprint_ConnectionBinding,
    Footprint_DatabaseSnapshot,
    Footprint_ImageSnapshot,
    Footprint_VoiceSnapshot,
    Footprint_FavoriteSnapshot,
    Footprint_Decompression,
    Footprint_PackagePreparation,
    Footprint_PackageEncryption,
    Footprint_Upload,
    Footprint_UploadCommit,
    Footprint_SourceCleanup,
    Footprint_ReceiverTracking
}
