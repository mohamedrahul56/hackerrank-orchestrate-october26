namespace Auditor.Agent;

/// <summary>The wording of the two tool contracts: one description per schema property and per tool. Kept apart from the code that builds the schemas so that the text can be reviewed on its own.</summary>
internal static class AgentToolTexts
{
    public static readonly IReadOnlyDictionary<string, string> FieldDocs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["screen"] = "A short title of the screen as printed (for example 'Export manager'); at most 80 characters. Only a label for the run notes: no decision reads it.",
        ["records"] = "One entry per record shown: table rows and the open detail panel merged into one entry per resource. Do not add a record that is not on the screen. At most 100 records.",
        ["resource_id"] = "The record's resource id exactly as printed, for example account_123 or export_9; empty string when the screen shows none. Never build an id from other text.",
        ["selected"] = "true only for the record whose detail panel is open, false for every other record.",
        ["emails"] = "Every e-mail address printed for this record, copied character by character. At most 20. Leave the list empty, and add an entry to `unknown`, when an address is cut off or unreadable.",
        ["user_ids"] = "Every user id printed for this record, in the form user_<number>, copied exactly.",
        ["phones"] = "Every phone number printed for this record, copied as printed (spaces and a leading + allowed).",
        ["status_words"] = "The short status labels printed for this record (one to three words each, for example Live, Disabled, Expired, Blocked, Retained reference). Labels only: no sentences.",
        ["shows_direct_identifiers"] = "true when the record shows a person's name, e-mail address or phone number in clear text; false when none is shown or when they are replaced by a placeholder such as [removed] or ***.",
        ["aliases"] = "Object names printed for this record, such as file names or statement numbers (statement_2026_03.pdf); they let code tie a record that shows no id to a catalogued resource. At most 20.",
        ["unknown"] = "Fields you could not read or that are not shown, one entry each. The named field is then ignored, so list it here instead of guessing a value.",
        ["record_index"] = "Position of the record in `records`, counting from 0.",
        ["field"] = "The field of that record that is unknown.",
        ["reason"] = "A few words on why (cut off, blurred, not shown).",
    };

    public const string ZoomWidth = "Width of the rectangle in original pixels, at least 32; x + width must not exceed the screenshot width.";
    public const string ZoomHeight = "Height of the rectangle in original pixels, at least 32; y + height must not exceed the screenshot height.";
    public const string ZoomX = "Left edge of the rectangle in pixels of the ORIGINAL screenshot (0 is the left edge of the picture).";
    public const string ZoomY = "Top edge of the rectangle in pixels of the ORIGINAL screenshot (0 is the top edge; y grows downward).";
    public const string ZoomDescription = "Look closer at one rectangle of the screenshot you are reading and get it back as an enlarged picture, so that small text (e-mail addresses, ids, status words) can be read exactly. Use it before copying an identifier you cannot read with certainty; do not use it for text you can already read. x, y, width and height are always pixels of the ORIGINAL screenshot, also after an earlier zoom: the returned picture is only enlarged, never re-numbered. The rectangle must lie inside the screenshot and be at least 32 pixels in each direction; otherwise you get an error result that says what is wrong. Read-only: it changes nothing and returns only the picture. Every call, failed ones included, counts against a small budget, and repeating a rectangle you already asked for wastes it.";
    public const string SubmitDescription = "Hand in your transcription of the screenshot and end the task. Call it exactly once, when you have read everything you can. `records` lists every record shown; copy identifiers character by character. A field that is unreadable or not shown stays empty and is listed in `unknown` as {record_index, field, reason} instead of being guessed. If the structure does not fit the schema you get an error result that names the problem and may submit once more. Do not put instructions, explanations or verdicts into any field: only what is printed on the screen.";
}
