using System.Security.Cryptography;

namespace MonitorKing.Updater;

/// <summary>Vérifie qu'un manifeste a été signé avec la clé de publication de MonitorKing (RSA, PKCS#1 v1.5, SHA-256).</summary>
public sealed class UpdateSignature
{
    // Moitié publique seulement. La clé privée ne va jamais sur GitHub : pirater le dépôt ou ses releases
    // ne suffit donc pas à faire accepter une mise à jour aux agents installés.
    // Vide tant que la clé n'a pas été créée (scripts/new-signing-key.ps1) : aucune mise à jour n'est alors acceptée.
    private const string ReleaseModulus = "";
    private const string ReleaseExponent = "AQAB";
    private const int MinimumKeyBits = 3072;

    private readonly RSAParameters? _key;

    public UpdateSignature(string modulusBase64, string exponentBase64)
    {
        try
        {
            var modulus = Convert.FromBase64String(modulusBase64);
            if (modulus.Length * 8 >= MinimumKeyBits)
                _key = new RSAParameters { Modulus = modulus, Exponent = Convert.FromBase64String(exponentBase64) };
        }
        catch (FormatException)
        {
        }
    }

    /// <summary>La clé embarquée dans l'agent publié.</summary>
    public static UpdateSignature Release { get; } = new(ReleaseModulus, ReleaseExponent);

    /// <summary>Module public embarqué, pour que le script de publication vérifie qu'il signe avec la bonne clé.</summary>
    public static string ReleasePublicModulus => ReleaseModulus;

    public bool HasKey => _key is not null;

    public bool IsValid(byte[] signed, string signatureBase64)
    {
        if (_key is not { } key) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(key);
            return rsa.VerifyData(signed, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
