namespace EasyPDF.Core;

/// <summary>
/// Thrown by <see cref="Interfaces.IPdfDocumentService"/> when the PDF requires a password.
/// <see cref="IsWrongPassword"/> distinguishes "no password provided" from "wrong password".
/// </summary>
public sealed class PdfPasswordRequiredException : Exception
{
    public bool IsWrongPassword { get; }

    public PdfPasswordRequiredException(bool isWrongPassword = false)
        : base(isWrongPassword ? "Incorrect password." : "This PDF is password-protected.")
    {
        IsWrongPassword = isWrongPassword;
    }
}
