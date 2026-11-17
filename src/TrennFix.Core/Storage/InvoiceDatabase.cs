using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrennFix.Core.Models;

namespace TrennFix.Core.Storage;

/// <summary>
/// SQLite database that indexes all archived invoices, suppliers and already processed mails.
/// It lives next to the archive folder (so archive + index always belong together).
/// </summary>
/// <summary>Documents of an invoice that is listed but not yet written to the archive folder.</summary>
public sealed record PendingFiles(byte[] Main, string MainExtension, byte[]? Xml, byte[]? Mail, string MailExtension);

public sealed class InvoiceDatabase : IDisposable
{
    private readonly SqliteConnection _db;

    public InvoiceDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public void Dispose() => _db.Dispose();

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS suppliers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                folder_name TEXT NOT NULL,
                vat_id TEXT,
                tax_number TEXT
            );
            CREATE TABLE IF NOT EXISTS supplier_emails (
                supplier_id INTEGER NOT NULL REFERENCES suppliers(id) ON DELETE CASCADE,
                address TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (supplier_id, address)
            );
            CREATE TABLE IF NOT EXISTS supplier_ibans (
                supplier_id INTEGER NOT NULL REFERENCES suppliers(id) ON DELETE CASCADE,
                iban TEXT NOT NULL,
                confirmed INTEGER NOT NULL DEFAULT 0,
                first_seen TEXT NOT NULL,
                PRIMARY KEY (supplier_id, iban)
            );
            CREATE TABLE IF NOT EXISTS invoices (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                supplier_id INTEGER NOT NULL REFERENCES suppliers(id),
                invoice_number TEXT,
                invoice_date TEXT,
                due_date TEXT,
                seller_name TEXT,
                seller_vat_id TEXT,
                seller_tax_number TEXT,
                net_amount TEXT,
                tax_amount TEXT,
                gross_amount TEXT,
                currency TEXT,
                payment_method INTEGER NOT NULL,
                iban TEXT,
                bic TEXT,
                payment_reference TEXT,
                mandate_reference TEXT,
                creditor_id TEXT,
                discount_terms TEXT,
                kind INTEGER NOT NULL,
                source INTEGER NOT NULL,
                format_name TEXT,
                status INTEGER NOT NULL,
                warnings TEXT NOT NULL,
                file_path TEXT NOT NULL,
                xml_file_path TEXT,
                mail_file_path TEXT,
                file_sha256 TEXT NOT NULL,
                original_file_name TEXT NOT NULL,
                mail_message_id TEXT NOT NULL,
                mail_subject TEXT NOT NULL,
                mail_from TEXT NOT NULL,
                mail_received TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                printed INTEGER NOT NULL DEFAULT 0,
                raw_text TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_invoices_supplier_number ON invoices(supplier_id, invoice_number);
            CREATE INDEX IF NOT EXISTS ix_invoices_sha ON invoices(file_sha256);
            CREATE TABLE IF NOT EXISTS pending_files (
                invoice_id INTEGER PRIMARY KEY REFERENCES invoices(id) ON DELETE CASCADE,
                main BLOB NOT NULL,
                main_ext TEXT NOT NULL,
                xml BLOB,
                mail BLOB,
                mail_ext TEXT
            );
            CREATE TABLE IF NOT EXISTS processed_messages (
                message_id TEXT PRIMARY KEY,
                received TEXT NOT NULL,
                processed_at TEXT NOT NULL,
                invoice_count INTEGER NOT NULL,
                note TEXT
            );
            """);
    }

    // ------------------------------------------------------------------ processed mails

    public bool IsMessageProcessed(string messageId) =>
        Scalar<long>("SELECT COUNT(*) FROM processed_messages WHERE message_id = $id", ("$id", messageId)) > 0;

    public void MarkMessageProcessed(string messageId, DateTimeOffset received, int invoiceCount, string? note) =>
        Exec("""
            INSERT OR REPLACE INTO processed_messages(message_id, received, processed_at, invoice_count, note)
            VALUES ($id, $r, $p, $c, $n)
            """, ("$id", messageId), ("$r", received.ToString("O")), ("$p", DateTimeOffset.Now.ToString("O")), ("$c", invoiceCount), ("$n", note));

    // ------------------------------------------------------------------ suppliers

    public List<Supplier> GetSuppliers()
    {
        var result = new Dictionary<long, Supplier>();
        using (var r = Query("SELECT id, name, folder_name, vat_id, tax_number FROM suppliers ORDER BY name"))
            while (r.Read())
                result[r.GetInt64(0)] = new Supplier
                {
                    Id = r.GetInt64(0), Name = r.GetString(1), FolderName = r.GetString(2),
                    VatId = r.IsDBNull(3) ? null : r.GetString(3), TaxNumber = r.IsDBNull(4) ? null : r.GetString(4),
                };
        using (var r = Query("SELECT supplier_id, address FROM supplier_emails"))
            while (r.Read())
                if (result.TryGetValue(r.GetInt64(0), out var s)) s.EmailAddresses.Add(r.GetString(1));
        using (var r = Query("SELECT supplier_id, iban FROM supplier_ibans WHERE confirmed = 1"))
            while (r.Read())
                if (result.TryGetValue(r.GetInt64(0), out var s)) s.KnownIbans.Add(r.GetString(1));
        return result.Values.ToList();
    }

    public Supplier CreateSupplier(string name, string folderName, string? vatId, string? taxNumber)
    {
        var id = Scalar<long>("""
            INSERT INTO suppliers(name, folder_name, vat_id, tax_number) VALUES ($n, $f, $v, $t);
            SELECT last_insert_rowid();
            """, ("$n", name), ("$f", folderName), ("$v", vatId), ("$t", taxNumber));
        return new Supplier { Id = id, Name = name, FolderName = folderName, VatId = vatId, TaxNumber = taxNumber };
    }

    public void UpdateSupplierIds(long supplierId, string? vatId, string? taxNumber) =>
        Exec("""
            UPDATE suppliers SET vat_id = COALESCE(vat_id, $v), tax_number = COALESCE(tax_number, $t) WHERE id = $id
            """, ("$id", supplierId), ("$v", vatId), ("$t", taxNumber));

    public void RenameSupplier(long supplierId, string name) =>
        Exec("UPDATE suppliers SET name = $n WHERE id = $id", ("$id", supplierId), ("$n", name));

    public void AddSupplierEmail(long supplierId, string address) =>
        Exec("INSERT OR IGNORE INTO supplier_emails(supplier_id, address) VALUES ($id, $a)", ("$id", supplierId), ("$a", address.ToLowerInvariant()));

    public void AddSupplierIban(long supplierId, string iban, bool confirmed) =>
        Exec("""
            INSERT INTO supplier_ibans(supplier_id, iban, confirmed, first_seen) VALUES ($id, $i, $c, $d)
            ON CONFLICT(supplier_id, iban) DO UPDATE SET confirmed = MAX(confirmed, excluded.confirmed)
            """, ("$id", supplierId), ("$i", iban), ("$c", confirmed ? 1 : 0), ("$d", DateTimeOffset.Now.ToString("O")));

    // ------------------------------------------------------------------ invoices

    public InvoiceRecord? FindDuplicate(long supplierId, string? invoiceNumber, string sha256)
    {
        var id = Scalar<long?>("""
            SELECT id FROM invoices
            WHERE file_sha256 = $sha
               OR ($nr IS NOT NULL AND supplier_id = $sid AND invoice_number = $nr AND kind IN (0, 1))
            ORDER BY id LIMIT 1
            """, ("$sha", sha256), ("$sid", supplierId), ("$nr", invoiceNumber));
        return id is { } i ? GetInvoice(i) : null;
    }

    public bool ShaExists(string sha256) => Scalar<long>("SELECT COUNT(*) FROM invoices WHERE file_sha256 = $s", ("$s", sha256)) > 0;

    public long InsertInvoice(InvoiceRecord r)
    {
        var d = r.Data;
        r.Id = Scalar<long>("""
            INSERT INTO invoices(supplier_id, invoice_number, invoice_date, due_date, seller_name, seller_vat_id, seller_tax_number,
                net_amount, tax_amount, gross_amount, currency, payment_method, iban, bic, payment_reference, mandate_reference,
                creditor_id, discount_terms, kind, source, format_name, status, warnings, file_path, xml_file_path, mail_file_path,
                file_sha256, original_file_name, mail_message_id, mail_subject, mail_from, mail_received, imported_at, printed, raw_text)
            VALUES ($sid, $nr, $date, $due, $sname, $svat, $stax, $net, $tax, $gross, $cur, $pm, $iban, $bic, $pref, $mandate,
                $cred, $disc, $kind, $src, $fmt, $status, $warn, $file, $xml, $mail, $sha, $orig, $mid, $subj, $from, $recv, $imp, $printed, $raw);
            SELECT last_insert_rowid();
            """,
            ("$sid", r.SupplierId), ("$nr", d.InvoiceNumber), ("$date", D(d.InvoiceDate)), ("$due", D(d.DueDate)),
            ("$sname", d.SellerName), ("$svat", d.SellerVatId), ("$stax", d.SellerTaxNumber),
            ("$net", M(d.NetAmount)), ("$tax", M(d.TaxAmount)), ("$gross", M(d.GrossAmount)), ("$cur", d.Currency),
            ("$pm", (int)d.PaymentMethod), ("$iban", d.Iban), ("$bic", d.Bic), ("$pref", d.PaymentReference),
            ("$mandate", d.MandateReference), ("$cred", d.CreditorId), ("$disc", d.DiscountTerms), ("$kind", (int)d.Kind),
            ("$src", (int)d.Source), ("$fmt", d.FormatName), ("$status", (int)r.Status), ("$warn", JsonSerializer.Serialize(r.Warnings)),
            ("$file", r.FilePath), ("$xml", r.XmlFilePath), ("$mail", r.MailFilePath), ("$sha", r.FileSha256),
            ("$orig", r.OriginalFileName), ("$mid", r.MailMessageId), ("$subj", r.MailSubject), ("$from", r.MailFrom),
            ("$recv", r.MailReceived.ToString("O")), ("$imp", r.ImportedAt.ToString("O")), ("$printed", r.Printed ? 1 : 0), ("$raw", d.RawText));
        return r.Id;
    }

    public void UpdateStatus(long invoiceId, InvoiceStatus status) =>
        Exec("UPDATE invoices SET status = $s WHERE id = $id", ("$id", invoiceId), ("$s", (int)status));

    // ------------------------------------------------------------------ not yet saved documents

    public void SavePending(long invoiceId, PendingFiles f) =>
        Exec("""
            INSERT OR REPLACE INTO pending_files(invoice_id, main, main_ext, xml, mail, mail_ext)
            VALUES ($id, $m, $me, $x, $ml, $mle)
            """, ("$id", invoiceId), ("$m", f.Main), ("$me", f.MainExtension), ("$x", f.Xml), ("$ml", f.Mail), ("$mle", f.MailExtension));

    public PendingFiles? GetPending(long invoiceId)
    {
        using var r = Query("SELECT main, main_ext, xml, mail, mail_ext FROM pending_files WHERE invoice_id = $id", ("$id", invoiceId));
        if (!r.Read()) return null;
        return new PendingFiles(
            (byte[])r[0], r.GetString(1),
            r.IsDBNull(2) ? null : (byte[])r[2],
            r.IsDBNull(3) ? null : (byte[])r[3],
            r.IsDBNull(4) ? ".eml" : r.GetString(4));
    }

    /// <summary>Documents were written to the archive folder → remember the paths, drop the copy in the database.</summary>
    public void MarkSaved(long invoiceId, string filePath, string? xmlPath, string? mailPath)
    {
        Exec("UPDATE invoices SET file_path = $f, xml_file_path = $x, mail_file_path = $m WHERE id = $id",
            ("$id", invoiceId), ("$f", filePath), ("$x", xmlPath), ("$m", mailPath));
        Exec("DELETE FROM pending_files WHERE invoice_id = $id", ("$id", invoiceId));
    }

    /// <summary>
    /// Removes every invoice that was listed but not saved, and forgets that its mails were processed –
    /// the next scan of that period reads them again (useful while testing).
    /// </summary>
    public int DiscardUnsaved()
    {
        Exec("BEGIN");
        Exec("DELETE FROM processed_messages WHERE message_id IN (SELECT mail_message_id FROM invoices WHERE file_path = '')");
        var count = Scalar<long>("SELECT COUNT(*) FROM invoices WHERE file_path = ''");
        Exec("DELETE FROM pending_files WHERE invoice_id IN (SELECT id FROM invoices WHERE file_path = '')");
        Exec("DELETE FROM invoices WHERE file_path = ''");
        // suppliers (and the IBANs learned from them) that only existed because of discarded invoices
        Exec("DELETE FROM suppliers WHERE id NOT IN (SELECT supplier_id FROM invoices)");
        // mails without any invoice (no attachment etc.) are also read again
        Exec("DELETE FROM processed_messages WHERE invoice_count = 0");
        Exec("COMMIT");
        return (int)count;
    }

    public void MarkPrinted(long invoiceId) => Exec("UPDATE invoices SET printed = 1 WHERE id = $id", ("$id", invoiceId));

    /// <summary>User confirmed a (new) IBAN of a supplier → remove the warning on future invoices.</summary>
    public void ConfirmIban(long invoiceId)
    {
        var inv = GetInvoice(invoiceId);
        if (inv?.Data.Iban is null) return;
        AddSupplierIban(inv.SupplierId, inv.Data.Iban, confirmed: true);
        var remaining = inv.Warnings.Where(w => !w.StartsWith("ACHTUNG: IBAN", StringComparison.Ordinal)).ToList();
        Exec("UPDATE invoices SET warnings = $w, status = CASE WHEN status = 1 AND $empty = 1 THEN 0 ELSE status END WHERE id = $id",
            ("$id", invoiceId), ("$w", JsonSerializer.Serialize(remaining)), ("$empty", remaining.Count == 0 ? 1 : 0));
    }

    public InvoiceRecord? GetInvoice(long id) => GetInvoices("WHERE i.id = $id", ("$id", id)).FirstOrDefault();

    public List<InvoiceRecord> GetInvoices(string where = "", params (string, object?)[] args)
    {
        var list = new List<InvoiceRecord>();
        using var r = Query($"""
            SELECT i.id, i.supplier_id, s.name, i.invoice_number, i.invoice_date, i.due_date, i.seller_name, i.seller_vat_id,
                   i.seller_tax_number, i.net_amount, i.tax_amount, i.gross_amount, i.currency, i.payment_method, i.iban, i.bic,
                   i.payment_reference, i.mandate_reference, i.creditor_id, i.discount_terms, i.kind, i.source, i.format_name,
                   i.status, i.warnings, i.file_path, i.xml_file_path, i.mail_file_path, i.file_sha256, i.original_file_name,
                   i.mail_message_id, i.mail_subject, i.mail_from, i.mail_received, i.imported_at, i.printed
            FROM invoices i JOIN suppliers s ON s.id = i.supplier_id
            {where}
            ORDER BY i.mail_received DESC, i.id DESC
            """, args);
        while (r.Read())
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var rec = new InvoiceRecord
            {
                Id = r.GetInt64(0),
                SupplierId = r.GetInt64(1),
                SupplierName = r.GetString(2),
                Data = new InvoiceData
                {
                    InvoiceNumber = S(3), InvoiceDate = PD(S(4)), DueDate = PD(S(5)), SellerName = S(6), SellerVatId = S(7),
                    SellerTaxNumber = S(8), NetAmount = PM(S(9)), TaxAmount = PM(S(10)), GrossAmount = PM(S(11)),
                    Currency = S(12) ?? "EUR", PaymentMethod = (PaymentMethod)r.GetInt32(13), Iban = S(14), Bic = S(15),
                    PaymentReference = S(16), MandateReference = S(17), CreditorId = S(18), DiscountTerms = S(19),
                    Kind = (DocumentKind)r.GetInt32(20), Source = (ExtractionSource)r.GetInt32(21), FormatName = S(22),
                },
                Status = (InvoiceStatus)r.GetInt32(23),
                FilePath = r.GetString(25), XmlFilePath = S(26), MailFilePath = S(27), FileSha256 = r.GetString(28),
                OriginalFileName = r.GetString(29), MailMessageId = r.GetString(30), MailSubject = r.GetString(31),
                MailFrom = r.GetString(32), MailReceived = DateTimeOffset.Parse(r.GetString(33), CultureInfo.InvariantCulture),
                ImportedAt = DateTimeOffset.Parse(r.GetString(34), CultureInfo.InvariantCulture), Printed = r.GetInt32(35) == 1,
            };
            rec.Warnings.AddRange(JsonSerializer.Deserialize<List<string>>(r.GetString(24)) ?? []);
            list.Add(rec);
        }
        return list;
    }

    // ------------------------------------------------------------------ helpers

    private static string? D(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly? PD(string? s) => s is null ? null : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? M(decimal? m) => m?.ToString(CultureInfo.InvariantCulture);
    private static decimal? PM(string? s) => s is null ? null : decimal.Parse(s, CultureInfo.InvariantCulture);

    private SqliteCommand Cmd(string sql, (string, object?)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(v, target, CultureInfo.InvariantCulture);
    }

    private SqliteDataReader Query(string sql, params (string, object?)[] args)
    {
        var cmd = Cmd(sql, args);
        return cmd.ExecuteReader(System.Data.CommandBehavior.Default);
    }
}
