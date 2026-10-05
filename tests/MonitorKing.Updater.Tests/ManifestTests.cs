using System.Text;
using MonitorKing.Updater;

namespace MonitorKing.Updater.Tests;

public class ManifestTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void Lit_la_version_et_les_fichiers()
    {
        var manifest = UpdateManifest.Parse(Ascii($"version=1.2.3\nMonitorKing-Agent-v1.2.3-win-x64.zip=50458545:{Hash}\nautre.txt=7:{Hash}\n"));

        Assert.Equal(new Version(1, 2, 3), manifest.Version);
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(new UpdateFile("MonitorKing-Agent-v1.2.3-win-x64.zip", 50458545, Hash), manifest.Files[0]);
    }

    [Theory]
    [InlineData("version=1.2.3\r\na.zip=7:{H}\r\n")]            // fins de ligne Windows
    [InlineData("version=1.2.3\n\na.zip=7:{H}\n")]              // ligne vide
    [InlineData("version=1.2.3\na.zip=7:{H}\nnote=bonjour\n")]  // ligne inconnue
    [InlineData("a.zip=7:{H}\nversion=1.2.3\n")]                // version pas en premier
    [InlineData("version=1.2.3\n")]                             // aucun fichier
    [InlineData("version=1.02.3\na.zip=7:{H}\n")]               // version non canonique
    [InlineData("version=1.2\na.zip=7:{H}\n")]
    [InlineData("version=1.2.3.4\na.zip=7:{H}\n")]
    [InlineData("version=v1.2.3\na.zip=7:{H}\n")]
    [InlineData("version=1.2.3\na.zip=7:{H}\na.zip=7:{H}\n")]   // fichier en double
    [InlineData("version=1.2.3\nA.ZIP=7:{H}\na.zip=7:{H}\n")]   // même nom, autre casse
    [InlineData("version=1.2.3\na.zip=0:{H}\n")]                // taille nulle
    [InlineData("version=1.2.3\na.zip=07:{H}\n")]
    [InlineData("version=1.2.3\na.zip=-7:{H}\n")]
    [InlineData("version=1.2.3\na.zip=7:ABCDEF\n")]             // empreinte trop courte
    [InlineData("version=1.2.3\ndossier/a.zip=7:{H}\n")]        // chemin
    [InlineData("version=1.2.3\n..\\a.zip=7:{H}\n")]
    [InlineData("version=1.2.3\n.cache=7:{H}\n")]
    [InlineData("version=1.2.3\nversion=7:{H}\n")]              // nom réservé
    [InlineData("version=1.2.3\na.zip=7:{H}")]                  // dernière ligne non terminée
    [InlineData("version=1.2.3\na.zip=7:{H} \n")]               // espace en trop
    [InlineData("version=1.2.3\né.zip=7:{H}\n")]           // hors ASCII
    public void Refuse_tout_ecart_au_format(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text.Replace("{H}", Hash));

        Assert.Throws<FormatException>(() => UpdateManifest.Parse(bytes));
    }

    [Fact]
    public void Refuse_une_empreinte_en_majuscules_et_un_BOM()
    {
        Assert.Throws<FormatException>(() => UpdateManifest.Parse(Ascii($"version=1.2.3\na.zip=7:{Hash.ToUpperInvariant()}\n")));
        Assert.Throws<FormatException>(() => UpdateManifest.Parse(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Ascii($"version=1.2.3\na.zip=7:{Hash}\n")).ToArray()));
    }

    [Fact]
    public void Refuse_plus_de_seize_fichiers_et_un_manifeste_trop_gros()
    {
        var many = "version=1.2.3\n" + string.Concat(Enumerable.Range(0, 17).Select(i => $"f{i}.zip=7:{Hash}\n"));
        Assert.Throws<FormatException>(() => UpdateManifest.Parse(Ascii(many)));
        Assert.Throws<FormatException>(() => UpdateManifest.Parse(new byte[UpdateManifest.MaxBytes + 1]));
    }

    [Fact]
    public void Separe_la_signature_du_contenu_signe()
    {
        var body = Ascii($"version=1.2.3\na.zip=7:{Hash}\n");
        var raw = Ascii("signature=QUJDREVGR0hJSktMTU5PUA==\n").Concat(body).ToArray();

        Assert.True(UpdateManifest.TrySplit(raw, out var signature, out var signed));
        Assert.Equal("QUJDREVGR0hJSktMTU5PUA==", signature);
        Assert.Equal(body, signed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("version=1.2.3\n")]                         // pas de ligne de signature
    [InlineData("signature=QUJDREVGR0hJSktMTU5PUA==\n")]    // rien de signé
    [InlineData("signature=QUJDREVGR0hJSktMTU5PUA==")]      // pas de saut de ligne
    [InlineData("signature=pas du base64 !!\nversion=1.2.3\n")]
    [InlineData("Signature=QUJDREVGR0hJSktMTU5PUA==\nversion=1.2.3\n")]
    [InlineData(" signature=QUJDREVGR0hJSktMTU5PUA==\nversion=1.2.3\n")]
    public void Refuse_une_ligne_de_signature_mal_formee(string text)
    {
        Assert.False(UpdateManifest.TrySplit(Ascii(text), out _, out _));
    }

    [Fact]
    public void Refuse_un_fichier_plus_gros_que_la_limite()
    {
        Assert.False(UpdateManifest.TrySplit(new byte[UpdateManifest.MaxBytes + 1], out _, out _));
    }
}
