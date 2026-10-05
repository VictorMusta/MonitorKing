using System.Security.Cryptography;
using System.Text;
using MonitorKing.Updater;

namespace MonitorKing.Updater.Tests;

public class SignatureTests
{
    private static readonly byte[] Signed = Encoding.ASCII.GetBytes("version=1.2.3\na.zip=7:00\n");

    [Fact]
    public void Accepte_la_signature_de_la_cle_de_publication()
    {
        Assert.True(TestKey.Publisher.Signature.IsValid(Signed, TestKey.Publisher.Sign(Signed)));
    }

    [Fact]
    public void Refuse_un_contenu_modifie_d_un_seul_octet()
    {
        var signature = TestKey.Publisher.Sign(Signed);
        var altered = (byte[])Signed.Clone();
        altered[8] ^= 1;

        Assert.False(TestKey.Publisher.Signature.IsValid(altered, signature));
    }

    [Fact]
    public void Refuse_la_signature_d_une_autre_cle()
    {
        Assert.False(TestKey.Publisher.Signature.IsValid(Signed, TestKey.Stranger.Sign(Signed)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du base64")]
    [InlineData("QUJD")]
    public void Refuse_une_signature_illisible(string signature)
    {
        Assert.False(TestKey.Publisher.Signature.IsValid(Signed, signature));
    }

    [Fact]
    public void Sans_cle_embarquee_rien_n_est_accepte()
    {
        var none = new UpdateSignature("", "AQAB");

        Assert.False(none.HasKey);
        Assert.False(none.IsValid(Signed, TestKey.Publisher.Sign(Signed)));
    }

    [Fact]
    public void La_cle_de_publication_embarquee_est_exploitable_et_n_accepte_pas_une_autre_cle()
    {
        Assert.True(UpdateSignature.Release.HasKey);
        Assert.Equal(512, UpdateSignature.ReleasePublicModulus.Length);
        Assert.False(UpdateSignature.Release.IsValid(Signed, TestKey.Publisher.Sign(Signed)));
    }

    [Fact]
    public void Refuse_une_cle_de_moins_de_3072_bits()
    {
        using var weak = RSA.Create(2048);
        var parameters = weak.ExportParameters(false);
        var signature = Convert.ToBase64String(weak.SignData(Signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var verifier = new UpdateSignature(Convert.ToBase64String(parameters.Modulus!), Convert.ToBase64String(parameters.Exponent!));

        Assert.False(verifier.HasKey);
        Assert.False(verifier.IsValid(Signed, signature));
    }
}
