using System.Xml.Linq;
using FaraVrcPhotoServer;
using Xunit;

namespace FaraVrcPhotoServer.Core.Tests;

/// <summary>
/// 写真に書くアバターの項目の形式（faratag。rdf:Description の中に、子要素で 1 項目ずつ）と、v1.0.0 の旧形式（faraxml の属性）の読み取り・判定。
/// </summary>
public sealed class FaraTagFormatTests
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Tag = "http://fara-labs.com/ns/faratag/1.0/";

    private const string LegacyXmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
          <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description xmlns:faraxml="http://fara-labs.com/ns/faraxml/1.0/" faraxml:Version="1" faraxml:AvatarName="FaraRosellia" faraxml:AvatarId="avtr_4733f15d-2338-4359-85a0-c8bb756bfe1c" faraxml:Source="log"/>
          </rdf:RDF>
        </x:xmpmeta>
        """;

    [Fact]
    public void TheNewFormatIsOneDescriptionWithElementsInTheAgreedOrder()
    {
        var xmp = FaraXml.AddTo(null, new FaraXmlInfo("FaraRosellia", "avtr_4733f15d-2338-4359-85a0-c8bb756bfe1c", "log"))!;

        var description = XDocument.Parse(xmp).Descendants(Rdf + "Description").Single();
        Assert.Equal([Tag + "AvatarName", Tag + "AvatarID", Tag + "Source", Tag + "Version"], description.Elements().Select(e => e.Name));
        Assert.Equal(["FaraRosellia", "avtr_4733f15d-2338-4359-85a0-c8bb756bfe1c", "log", "2"], description.Elements().Select(e => e.Value));
        Assert.Empty(description.Attributes().Where(a => !a.IsNamespaceDeclaration));                       // 項目は属性ではなく、子要素
        Assert.Contains("<faratag:AvatarName>FaraRosellia</faratag:AvatarName>", xmp);
        Assert.DoesNotContain("faraxml", xmp);                                                              // 旧形式は、新しくは書かない
    }

    [Fact]
    public void AvatarIdIsLeftOutWhenTheLogDidNotGiveIt()
    {
        var xmp = FaraXml.AddTo(null, new FaraXmlInfo("FaraRosellia", null, "log"))!;

        var names = XDocument.Parse(xmp).Descendants(Rdf + "Description").Single().Elements().Select(e => e.Name.LocalName);
        Assert.Equal(["AvatarName", "Source", "Version"], names);
        Assert.Null(FaraXml.Read(xmp)!.AvatarId);
    }

    [Fact]
    public void ValuesAreEscapedAndRoundTrip()
    {
        var xmp = FaraXml.AddTo(null, new FaraXmlInfo("A&B <c> \"d\"", "avtr_x", "log"))!;

        XDocument.Parse(xmp);                                                                               // XML として正しい
        var read = FaraXml.Read(xmp)!;
        Assert.Equal(("A&B <c> \"d\"", "avtr_x", "log"), (read.AvatarName, read.AvatarId, read.Source));
    }

    [Fact]
    public void ThePhotoMadeByVersion100StillReadsAndCountsAsAlreadyTagged()
    {
        Assert.True(FaraXml.Has(LegacyXmp));
        var read = FaraXml.Read(LegacyXmp)!;
        Assert.Equal(("FaraRosellia", "avtr_4733f15d-2338-4359-85a0-c8bb756bfe1c", "log"), (read.AvatarName, read.AvatarId, read.Source));

        Assert.Null(FaraXml.AddTo(LegacyXmp, new FaraXmlInfo("Other", null, "log")));                       // 旧形式の写真には、二重に書かない
    }

    [Fact]
    public void ANewFormatPhotoIsAlsoNotWrittenTwice()
    {
        var once = FaraXml.AddTo(null, new FaraXmlInfo("FaraRosellia", null, "log"))!;

        Assert.True(FaraXml.Has(once));
        Assert.Null(FaraXml.AddTo(once, new FaraXmlInfo("Other", null, "log")));
    }

    [Fact]
    public void TheNewFormatIsAddedAfterAnExistingVrchatXmpWithoutChangingIt()
    {
        const string vrchat = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:xmp="http://ns.adobe.com/xap/1.0/">
                <rdf:Description>
                  <xmp:CreatorTool>VRChat</xmp:CreatorTool>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;

        var xmp = FaraXml.AddTo(vrchat, new FaraXmlInfo("FaraRosellia", "avtr_x", "log"))!;

        Assert.StartsWith(vrchat[..vrchat.LastIndexOf("</rdf:RDF>", StringComparison.Ordinal)], xmp);       // 元の部分は、1 文字も変えない
        Assert.Equal(2, XDocument.Parse(xmp).Descendants(Rdf + "Description").Count());                   // 元の Description のほかに、1 つ
        Assert.Equal("FaraRosellia", FaraXml.Read(xmp)!.AvatarName);
    }

    [Fact]
    public void TheOldAvatarIdSpellingInAnOldPhotoIsStillRead()
    {
        const string xmp = """<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description xmlns:faraxml="http://fara-labs.com/ns/faraxml/1.0/"><faraxml:AvatarName>Old</faraxml:AvatarName><faraxml:AvatarId>avtr_old</faraxml:AvatarId></rdf:Description></rdf:RDF>""";

        Assert.Equal(("Old", "avtr_old"), (FaraXml.Read(xmp)!.AvatarName, FaraXml.Read(xmp)!.AvatarId));
    }
}
