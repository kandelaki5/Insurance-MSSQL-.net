// Ported from src/compliance/sanctions.test.ts ("UN sanctions list").
using Insurance.Domain.Compliance;

namespace Insurance.Domain.Tests.Compliance;

public class SanctionsTests
{
    private const string Xml = """
        <?xml version="1.0"?>
        <CONSOLIDATED_LIST dateGenerated="2026-10-02T23:00:05.487Z">
          <INDIVIDUALS>
            <INDIVIDUAL>
              <DATAID>6907993</DATAID>
              <FIRST_NAME>ERIC</FIRST_NAME>
              <SECOND_NAME>BADEGE</SECOND_NAME>
              <UN_LIST_TYPE>DRC</UN_LIST_TYPE>
              <REFERENCE_NUMBER>CDi.001</REFERENCE_NUMBER>
              <LISTED_ON>2012-12-31-04:00</LISTED_ON>
              <NATIONALITY><VALUE>Democratic Republic of the Congo</VALUE></NATIONALITY>
              <INDIVIDUAL_ALIAS><QUALITY/><ALIAS_NAME/></INDIVIDUAL_ALIAS>
              <INDIVIDUAL_DATE_OF_BIRTH><TYPE_OF_DATE>EXACT</TYPE_OF_DATE><YEAR>1971</YEAR></INDIVIDUAL_DATE_OF_BIRTH>
              <INDIVIDUAL_DOCUMENT><TYPE_OF_DOCUMENT>Passport</TYPE_OF_DOCUMENT><NUMBER>AB-12 3456</NUMBER></INDIVIDUAL_DOCUMENT>
            </INDIVIDUAL>
            <INDIVIDUAL>
              <DATAID>1</DATAID>
              <FIRST_NAME>Giorgi</FIRST_NAME>
              <SECOND_NAME>Beridze</SECOND_NAME>
              <UN_LIST_TYPE>Test</UN_LIST_TYPE>
              <REFERENCE_NUMBER>T.001</REFERENCE_NUMBER>
              <INDIVIDUAL_ALIAS><QUALITY>Good</QUALITY><ALIAS_NAME>George Beridse</ALIAS_NAME></INDIVIDUAL_ALIAS>
              <INDIVIDUAL_DATE_OF_BIRTH><DATE>1980-05-01</DATE></INDIVIDUAL_DATE_OF_BIRTH>
            </INDIVIDUAL>
          </INDIVIDUALS>
          <ENTITIES>
            <ENTITY>
              <DATAID>6908402</DATAID>
              <FIRST_NAME>ADF</FIRST_NAME>
              <UN_LIST_TYPE>DRC</UN_LIST_TYPE>
              <REFERENCE_NUMBER>CDe.001</REFERENCE_NUMBER>
              <ENTITY_ALIAS><QUALITY>a.k.a.</QUALITY><ALIAS_NAME>Allied Democratic Forces</ALIAS_NAME></ENTITY_ALIAS>
              <ENTITY_ALIAS><QUALITY>f.k.a.</QUALITY><ALIAS_NAME>Forces D&#233;mocratiques Alli&#233;es</ALIAS_NAME></ENTITY_ALIAS>
            </ENTITY>
          </ENTITIES>
        </CONSOLIDATED_LIST>
        """;

    private static readonly UnList List = Sanctions.ParseUnList(Xml);

    [Fact(DisplayName = "parses individuals and entities with aliases, birth years and documents")]
    public void ParsesList()
    {
        Assert.Equal("2026-10-02T23:00:05.487Z", List.GeneratedAt);
        Assert.Equal(3, List.Entries.Count);
        var eric = List.Entries[0];
        Assert.Equal("ERIC BADEGE", eric.Name);
        Assert.Empty(eric.Aliases);
        Assert.Equal(["1971"], eric.BirthYears);
        Assert.Equal(["AB123456"], eric.Documents);
        Assert.Equal(["Democratic Republic of the Congo"], eric.Nationalities);
        Assert.Equal("2012-12-31", eric.ListedOn);
        var adf = List.Entries[2];
        Assert.Equal(SanctionKind.Entity, adf.Kind);
        Assert.Equal(["Allied Democratic Forces", "Forces Démocratiques Alliées"], adf.Aliases);
        Assert.Equal("1980", List.Entries[1].BirthYears[0]);
    }

    [Fact(DisplayName = "transliterates Georgian names and drops generic company words")]
    public void Transliterates()
    {
        Assert.Equal("giorgi beridze", Sanctions.TransliterateGeorgian("გიორგი ბერიძე"));
        Assert.Equal(["aliansi", "jgupi"], Sanctions.NameTokens("შპს „ალიანსი“ ჯგუფი"));
        Assert.Equal(["forces", "democratiques"], Sanctions.NameTokens("Forces Démocratiques"));
    }

    [Fact(DisplayName = "a Georgian client with the same name as a listed person is a possible match; birth year makes it strong")]
    public void GeorgianClientMatches()
    {
        var possible = Sanctions.ScreenSubject(new ScreeningSubject("ბერიძე გიორგი", SanctionKind.Individual), List.Entries);
        Assert.Single(possible);
        Assert.Equal("1", possible[0].DataId);
        Assert.True(possible[0].Strong); // both tokens matched exactly
        var strong = Sanctions.ScreenSubject(new ScreeningSubject("ბერიძე გიორგი", SanctionKind.Individual, BirthYear: "1980"), List.Entries);
        Assert.Equal([MatchReason.Name, MatchReason.BirthYear], strong[0].Reasons);
    }

    [Fact(DisplayName = "one shared common token is not a match; a document number alone is")]
    public void DocumentAloneMatches()
    {
        Assert.Empty(Sanctions.ScreenSubject(new ScreeningSubject("გიორგი კაპანაძე", SanctionKind.Individual), List.Entries));
        var byDocument = Sanctions.ScreenSubject(new ScreeningSubject("Nikoloz Tsereteli", SanctionKind.Individual, Documents: ["ab 12-3456"]), List.Entries);
        Assert.Single(byDocument);
        Assert.Equal([MatchReason.Document], byDocument[0].Reasons);
        Assert.True(byDocument[0].Strong);
    }

    [Fact(DisplayName = "entities match through aliases and only against entities")]
    public void EntitiesMatchThroughAliases()
    {
        var hit = Sanctions.ScreenSubject(new ScreeningSubject("Allied Democratic Forces Ltd", SanctionKind.Entity), List.Entries);
        Assert.Single(hit);
        Assert.Equal("Allied Democratic Forces", hit[0].MatchedName);
        Assert.Empty(Sanctions.ScreenSubject(new ScreeningSubject("Allied Democratic Forces", SanctionKind.Individual), List.Entries));
    }

    [Fact(DisplayName = "a near spelling counts as the same token")]
    public void NearSpelling()
    {
        SanctionEntry[] entries = [new("x", SanctionKind.Individual, "Mohammed Al Rashid", [], "T", "T.1", null, [], [], [])];
        var m = Sanctions.ScreenSubject(new ScreeningSubject("Mohamed Rashid", SanctionKind.Individual), entries);
        Assert.Single(m);
        Assert.True(m[0].Score >= 0.6m);
    }
}
