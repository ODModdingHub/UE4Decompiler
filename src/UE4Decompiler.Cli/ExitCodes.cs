namespace UE4Decompiler.Cli;

/// <summary>
/// Standardized application exit codes (Phase 3).
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int GeneralFailure = 1;
    public const int InvalidArguments = 2;
    public const int UnsupportedInput = 3;
    public const int AuthenticationOrEncryptionFailure = 4;
    public const int ParseFailure = 5;
    public const int OutputFailure = 6;
    public const int PartialRecovery = 7;
    public const int ValidationFailure = 8;
}
