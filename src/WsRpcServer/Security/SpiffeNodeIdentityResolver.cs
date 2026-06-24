using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace WsRpcServer.Security;

/// <summary>
/// Типовий <see cref="INodeIdentityResolver"/>: бере перший SAN URI (SPIFFE-стиль) як ім'я principal'а,
/// а SPKI-SHA-256 — як стабільний fallback-id.
/// </summary>
/// <remarks>
/// SAN URI читається з розширення Subject Alternative Name (OID 2.5.29.17). Якщо URI відсутній,
/// <see cref="NodeIdentity.SpiffeId"/> = <c>null</c>, і стабільним ідентифікатором лишається
/// <see cref="NodeIdentity.SpkiThumbprint"/>.
/// </remarks>
public sealed class SpiffeNodeIdentityResolver : INodeIdentityResolver
{
    /// <inheritdoc />
    public NodeIdentity Resolve(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var spki = NodeIdentity.ComputeSpkiThumbprint(certificate);
        var spiffeId = ExtractFirstUriSan(certificate);

        return new NodeIdentity(spiffeId, spki, certificate.Subject);
    }

    /// <summary>OID розширення Subject Alternative Name.</summary>
    private const string SubjectAltNameOid = "2.5.29.17";

    /// <summary>Контекстний тег [6] (uniformResourceIdentifier) у GeneralName.</summary>
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    /// <summary>
    /// Витягує перший SAN URI з сертифіката, або <c>null</c>, якщо його немає.
    /// </summary>
    /// <remarks>
    /// BCL (.NET 10) у <see cref="X509SubjectAlternativeNameExtension"/> вміє перелічувати лише DNS/IP,
    /// тож URI-імена (SPIFFE) розбираємо вручну з ASN.1: SAN = <c>SEQUENCE OF GeneralName</c>, де URI —
    /// контекстний тег <c>[6] IA5String</c>.
    /// </remarks>
    private static Uri? ExtractFirstUriSan(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (!string.Equals(extension.Oid?.Value, SubjectAltNameOid, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
                var sequence = reader.ReadSequence();
                while (sequence.HasData)
                {
                    if (sequence.PeekTag() == UriTag)
                    {
                        var uri = sequence.ReadCharacterString(UniversalTagNumber.IA5String, UriTag);
                        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                        {
                            return parsed;
                        }
                    }
                    else
                    {
                        sequence.ReadEncodedValue();
                    }
                }
            }
            catch (AsnContentException)
            {
                // Зіпсоване розширення — ігноруємо (ідентичність впаде на SPKI-fallback).
            }
        }

        return null;
    }
}
