using System.Text.Json;
using System.Xml.Linq;

namespace B1Budget.Api.Services.B1;

/// <summary>
/// Service Layer property names for the native budget objects. Resolved from the live $metadata
/// rather than hard-coded, because names differ slightly between B1 versions/localizations.
/// Any value can be pinned via the settings' field-map override.
/// </summary>
public class BudgetFieldMap
{
    public string ScenarioSet { get; set; } = "BudgetScenarios";
    public string ScenarioKey { get; set; } = "Numerator";
    public string ScenarioName { get; set; } = "Name";
    public string ScenarioStart { get; set; } = "StartofFiscalYear";
    public string? ScenarioRatio { get; set; } = "InitialRatioPercentage";

    public string BudgetSet { get; set; } = "Budgets";
    public string BudgetKey { get; set; } = "Numerator";
    public string BudgetAccount { get; set; } = "AccountCode";
    public string BudgetScenario { get; set; } = "BudgetScenario";
    public string? HeaderDebit { get; set; } = "TotalAnnualBudgetDebit";
    public string? HeaderCredit { get; set; } = "TotalAnnualBudgetCredit";
    public string Lines { get; set; } = "BudgetLines";
    public string LineDebit { get; set; } = "BudgetTotDebit";
    public string LineCredit { get; set; } = "BudgetTotCredit";

    /// <summary>Human-readable notes from resolution (which fields were guessed / missing).</summary>
    public List<string> Notes { get; set; } = new();
    public bool FromMetadata { get; set; }

    public static BudgetFieldMap Resolve(string edmx, string? overrideJson)
    {
        var map = new BudgetFieldMap { FromMetadata = true };
        var doc = XDocument.Parse(edmx);
        var types = doc.Descendants().Where(e => e.Name.LocalName is "EntityType" or "ComplexType")
            .GroupBy(e => (string?)e.Attribute("Name") ?? "")
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sets = doc.Descendants().Where(e => e.Name.LocalName == "EntitySet")
            .Select(e => (Name: (string?)e.Attribute("Name") ?? "", Type: ((string?)e.Attribute("EntityType") ?? "").Split('.').Last()))
            .ToList();

        static List<(string Name, string Type)> Props(XElement t) => t.Elements()
            .Where(e => e.Name.LocalName is "Property" or "NavigationProperty")
            .Select(e => ((string?)e.Attribute("Name") ?? "", (string?)e.Attribute("Type") ?? "")).ToList();

        // ---- scenario
        if (types.TryGetValue("BudgetScenario", out var scenType))
        {
            var p = Props(scenType).Select(x => x.Name).ToList();
            map.ScenarioSet = sets.FirstOrDefault(s => s.Type == "BudgetScenario").Name ?? map.ScenarioSet;
            map.ScenarioKey = KeyOf(scenType) ?? map.ScenarioKey;
            map.ScenarioName = Pick(p, map, "scenario name", ["Name", "ScenarioName"], n => n.Contains("Name")) ?? map.ScenarioName;
            map.ScenarioStart = Pick(p, map, "scenario fiscal-year start", ["StartofFiscalYear", "StartOfFiscalYear"], n => n.Contains("Start") && n.Contains("Year")) ?? map.ScenarioStart;
            map.ScenarioRatio = Pick(p, map, "scenario initial ratio", ["InitialRatioPercentage"], n => n.Contains("Ratio"), required: false);
        }
        else map.Notes.Add("EntityType BudgetScenario not found in $metadata — using defaults.");

        // ---- budget + lines
        if (types.TryGetValue("Budget", out var budType))
        {
            var props = Props(budType);
            var p = props.Select(x => x.Name).ToList();
            map.BudgetSet = sets.FirstOrDefault(s => s.Type == "Budget").Name ?? map.BudgetSet;
            map.BudgetKey = KeyOf(budType) ?? map.BudgetKey;
            map.BudgetAccount = Pick(p, map, "budget account", ["AccountCode"], n => n.Contains("Account")) ?? map.BudgetAccount;
            map.BudgetScenario = Pick(p, map, "budget scenario", ["BudgetScenario"], n => n.Contains("Scenario")) ?? map.BudgetScenario;
            map.HeaderDebit = Pick(p, map, "annual debit total", ["TotalAnnualBudgetDebit", "TotalAnnualBudgetDebitLoc"],
                n => n.Contains("Annual") && n.Contains("Debit") && IsLocal(n), required: false);
            map.HeaderCredit = Pick(p, map, "annual credit total", ["TotalAnnualBudgetCredit", "TotalAnnualBudgetCreditLoc"],
                n => n.Contains("Annual") && n.Contains("Credit") && IsLocal(n), required: false);

            var lineColl = props.FirstOrDefault(x => x.Type.StartsWith("Collection(", StringComparison.Ordinal));
            if (lineColl.Name is { Length: > 0 })
            {
                map.Lines = lineColl.Name;
                var lineTypeName = lineColl.Type["Collection(".Length..^1].Split('.').Last();
                if (types.TryGetValue(lineTypeName, out var lineType))
                {
                    var lp = Props(lineType).Select(x => x.Name).ToList();
                    map.LineDebit = Pick(lp, map, "period debit", ["BudgetTotDebit", "BudgetTotDebitLC", "DebitTotal"],
                        n => n.Contains("Deb") && IsLocal(n)) ?? map.LineDebit;
                    map.LineCredit = Pick(lp, map, "period credit", ["BudgetTotCredit", "BudgetTotCreditLC", "CreditTotal"],
                        n => n.Contains("Cred") && IsLocal(n)) ?? map.LineCredit;
                }
                else map.Notes.Add($"Line type {lineTypeName} not found — using default line field names.");
            }
            else map.Notes.Add("No line collection found on Budget — using default 'BudgetLines'.");
        }
        else map.Notes.Add("EntityType Budget not found in $metadata — using defaults.");

        return ApplyOverride(map, overrideJson);
    }

    public static BudgetFieldMap ApplyOverride(BudgetFieldMap map, string? overrideJson)
    {
        if (string.IsNullOrWhiteSpace(overrideJson)) return map;
        try
        {
            var o = JsonSerializer.Deserialize<Dictionary<string, string>>(overrideJson) ?? new();
            foreach (var (k, v) in o)
            {
                var prop = typeof(BudgetFieldMap).GetProperty(k);
                if (prop?.PropertyType == typeof(string)) { prop.SetValue(map, v); map.Notes.Add($"Override: {k} = {v}"); }
                else map.Notes.Add($"Override key '{k}' ignored (unknown).");
            }
        }
        catch (JsonException ex) { map.Notes.Add("Field-map override is not valid JSON: " + ex.Message); }
        return map;
    }

    // "Local currency" amount: skip system-currency, foreign-currency, percentage and future-expenditure fields.
    private static bool IsLocal(string n) =>
        !n.Contains("Sys", StringComparison.OrdinalIgnoreCase) && !n.Contains("FC", StringComparison.Ordinal) &&
        !n.Contains("Percent", StringComparison.OrdinalIgnoreCase) && !n.Contains("Future", StringComparison.OrdinalIgnoreCase) &&
        !n.Contains("Foreign", StringComparison.OrdinalIgnoreCase);

    private static string? KeyOf(XElement type) =>
        type.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Elements().FirstOrDefault()?.Attribute("Name")?.Value;

    private static string? Pick(List<string> props, BudgetFieldMap map, string label, string[] preferred, Func<string, bool> fallback, bool required = true)
    {
        foreach (var c in preferred)
            if (props.Contains(c)) return c;
        var guess = props.FirstOrDefault(fallback);
        if (guess != null) map.Notes.Add($"Guessed {label} field: {guess}");
        else if (required) map.Notes.Add($"Could not find a {label} field — using default.");
        return guess;
    }
}
