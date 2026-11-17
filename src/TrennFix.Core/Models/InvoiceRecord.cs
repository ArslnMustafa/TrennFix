namespace TrennFix.Core.Models;

public enum InvoiceStatus
{
    /// <summary>All important fields found.</summary>
    Ok = 0,
    /// <summary>Some fields missing or a warning was raised – please check manually.</summary>
    Pruefen = 1,
    /// <summary>Same invoice was already archived.</summary>
    Duplikat = 2,
    /// <summary>Marked as paid by the user.</summary>
    Bezahlt = 3,
}

/// <summary>An archived invoice as stored in the database and shown in the list.</summary>
public sealed class InvoiceRecord
{
    public long Id { get; set; }
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = "";

    public required InvoiceData Data { get; init; }

    public InvoiceStatus Status { get; set; }
    /// <summary>Human readable warnings, e.g. IBAN changed, fields missing.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Path in the archive folder; empty while the invoice is only listed ("nicht gespeichert").</summary>
    public string FilePath { get; set; } = "";
    public bool IsSaved => !string.IsNullOrEmpty(FilePath);
    public string? XmlFilePath { get; set; }
    public string? MailFilePath { get; set; }
    public string FileSha256 { get; set; } = "";
    public string OriginalFileName { get; set; } = "";

    public string MailMessageId { get; set; } = "";
    public string MailSubject { get; set; } = "";
    public string MailFrom { get; set; } = "";
    public DateTimeOffset MailReceived { get; set; }

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
    public bool Printed { get; set; }
}

public sealed class Supplier
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Folder name below the archive root.</summary>
    public string FolderName { get; set; } = "";
    public string? VatId { get; set; }
    public string? TaxNumber { get; set; }
    /// <summary>Sender e-mail addresses / domains we have seen for this supplier.</summary>
    public List<string> EmailAddresses { get; } = [];
    public List<string> KnownIbans { get; } = [];
}
