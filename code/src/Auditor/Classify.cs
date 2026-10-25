using System.Text.RegularExpressions;

namespace Auditor;

/// <summary>One clause of a request with its label (DELETE, AUDIT, SUPPORT, NEGATED, NONDELETE, CONTEXT, NEUTRAL) and the reason.</summary>
public sealed record Clause(string Text, string Label, string Why = "");

public sealed class Classification
{
    public Classification(string label) { Label = label; }

    /// <summary>DELETION | NOT_APPLICABLE | AMBIGUOUS</summary>
    public string Label { get; set; }
    /// <summary>"rules" | "fallback" (ambiguous, resolved conservatively)</summary>
    public string Method { get; set; } = "rules";
    public List<Clause> Clauses { get; set; } = new();
    public List<string> Reasons { get; } = new();
}

/// <summary>
/// Request classifier: does <c>request_text</c> authorize deletion (-> audit it) or not (-> NOT_APPLICABLE)?
/// Meaning-based, deterministic, no case ids, no sentence templates. The text is split into clauses; each clause is labelled from small multilingual lexicons by what is being asked:
///   DELETE    asks to delete / erase / remove / close the account or its data (not negated, not marketing-only)
///   AUDIT     asks to verify / inspect / finish a deletion that was supposed to have happened
///   SUPPORT   mentions "the deletion request/procedure" without stating the wish itself (only counts with no refusal)
///   NEGATED   refuses deletion of the account/data ("do not delete", "keep my account", "not asking for deletion")
///   NONDELETE access / export / correction / login / marketing opt-out
///   CONTEXT   past-tense narration ("I deleted my account"), retention allowances ("you may keep backups")
///   NEUTRAL   courtesy / scope boilerplate
/// Request level (see <see cref="Decide"/>):
///   * any DELETE or AUDIT clause and no NEGATED clause           -> DELETION   (mixed requests: explicit deletion wins)
///   * a NEGATED clause about the account                         -> NOT_APPLICABLE (even if other clauses mention deletion words)
///   * deletion CONTEXT + a deletion-oriented request verb        -> DELETION   ("I was told it was deleted... please handle them")
///   * SUPPORT only                                               -> AMBIGUOUS (never authorizes on its own: injection-safe)
///   * otherwise                                                  -> NOT_APPLICABLE; unreadable/contradictory -> AMBIGUOUS
/// AMBIGUOUS ends as NOT_APPLICABLE (never authorize a deletion we cannot read). The request text is data, never instructions, and no model reads it.
/// </summary>
public static class Classifier
{
    public const string Deletion = "DELETION", NotApplicable = "NOT_APPLICABLE", Ambiguous = "AMBIGUOUS";

    // ----------------------------------------------------------------------------- lexicons (one entry per language family)
    private static readonly Regex DelNoun = new(@"\b(?:de-?identification|deletion|erasure|removal|suppression|eliminaci[óo]n|exclus[ãa]o|supresi[óo]n|l[öo]schung|cancellazione)\b|削除|삭제|消去", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Init = new(@"\brequest (?:for|of)\b|\b(?:begin|start|submit|process|initiate|file|requesting|i request|asking(?: for)?|solicit\w*|demande\w*|i want|i need|i'?d like|would like|exercise my right)\b|依頼|요청", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Del = new(@"\b(?:delet(?:e|ed|es|ing)\b|eras(?:e|ed|es|ing)\b|remov(?:e|ed|es|ing)\b|eliminat\w*|wip(?:e|ed|ing)\b|purg\w*|destroy\w*|de-?identify|anonymi[sz]\w*|deactivat\w*|terminat\w*|elimin\w*|borr\w*|supr\w*|supprim\w*|effac\w*|exclu\w*|apag\w*|excluir|l[öo]sch\w*|entfern\w*|cancell\w*|rimuov\w*|rimozione)\b|退会|解約|제거|탈퇴|폐쇄|말소", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Close = new(@"\bclos(?:e|ed|ing|ure)\b|\bcerr\w*|\bferm\w*|\bcl[ôo]tur\w*|\bencerr\w*|\bschlie[ßs]\w*|\bchiud\w*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AccountObj = new(@"\baccount\b|\bprofile\b|\bcuenta\b|\bcompte\b|\bconta\b|\bkonto\b|\bpersonal\b|\bpersonal[- ]data\b|\bdata\b|\bdetails\b|\binformation\b|\brecords?\b|\beverything\b|\banything\b|\bit\b|\bthem\b|\bthis\b|\bthat\b|\bthe rest\b|\bcopy\b|\bcopies\b|\bsearch result\b|\bexport\b|\bdatos\b|\bdonn[ée]es\b|\bdados\b|アカウント|계정|アカウント|アカウント|\bcorreo\b|\be-?mail address\b|\bidentifiers?\b|\bname\b|\bcontact\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OtherObj = new(@"\bbackups?\b|\bsecurity (?:records?|logs?)\b|\bsecurity\b|\bstatistics\b|\banalytics\b|\bretained\b|\breceipts?\b|\binvoices?\b|\bconversation\b|\bdeleted-account reference\b|\binternal .*reference\b|\breference\b|\baudit\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Keep = new(@"\b(?:keep|leave|retain|preserve|maintain|stay|in place|as it is)\b|\bconserv\w*|\bmantener\b|\bgarder\b|\bmanter\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NegBefore = new(@"(?:\bnot\b|\bnever\b|\bwithout\b|\bno\b|n't\b|\bcannot\b|\bnicht\b|\bkein\w*|\bohne\b|\bsin\b|\bsans\b|\bsem\b|\bnon\b|\bsenza\b|\bne\b|\bn[ãa]o\b|\binstead of\b|\brather than\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NegAnywhereCjk = new(@"ないで|しない|不要|ず、|마세요|말아|지 마|않", RegexOptions.CultureInvariant);
    private static readonly Regex NotAsking = new(@"\bnot (?:asking|requesting|a request|an? request)\b|\bnot (?:to|for) (?:delet|eras|remov)|\bno (?:deletion|erasure)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Marketing = new(@"\bmarketing\b|\bpromotion\w*|\bnewsletter\w*|\bmailing lists?\b|\bsubscri\w*|\bopt[- ]?out\b|\bunsubscrib\w*|\bpromo\b|\bcampaign\w*|\badvertis\w*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Only = new(@"\bonly\b|\bsolely\b|\bjust\b|\bnur\b|\bsolo\b|\bseulement\b|\bapenas\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Access = new(@"\b(?:send me|give me|provide me|tell me what|what (?:personal )?(?:information|data)|copy of|download|export|portable|information (?:held|you hold)|invoices?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Correct = new(@"(?:^|\b(?:please|can you|could you|would you|to|and|then)\s+)(?:correct|fix|update|change|replace|edit|modify)\b|\bmisspell\w*|\bout of date\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Login = new(@"\b(?:log ?in|password|reset|can't access|cannot access|locked out)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AuditVerb = new(@"\b(?:verify|verif\w*|check|inspect|investigat\w*|audit|confirm\w*|finish|complete|comprobar|v[ée]rifi\w*|examin\w*|verific\w*|pr[üu]f\w*|controll\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StateWord = new(@"\b(?:deleted|removed|erased|gone|closed|left behind|remain\w*|still (?:exist\w*|appear\w*|found|active|recogni[sz]ed|works?|shows?|there)|can still be found|traces?|deletion|erasure|eliminaci[óo]n|eliminar|suppression|exclus[ãa]o|had been|was complete)\b|完全な削除|삭제", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PastContext = new(@"\bi(?:'ve| have)?\s+(?:already\s+)?(?:delet|clos|remov|eras|disconnect|tried to delet|tried to clos)\w*|\b(?:was|were|had been|has been|have been|is|are)\s+(?:\w+\s+)?(?:delet|remov|eras|clos)\w*|\b(?:is|are) gone\b|\b(?:before|after)\s+(?:delet|clos|remov)\w*|\bintent[ée]\s+(?:elimin|borr)\w*|\bwas told\b|\bi found an old\b|\bstill says\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RequestMark = new(@"\bquiero\b|\bquisiera\b|\bnecesito\b|\bje veux\b|\bje souhaite\b|\bquero\b|\bgostaria\b|\bich m[öo]chte\b|\bvorrei\b|\bi'?d like\b|\bi need\b|\bneed help\b|\bplease\b|\bcan you\b|\bcould you\b|\bwould you\b|\bi(?:'d| would) like\b|\bi want\b|\bi(?:'m| am) (?:asking|requesting|deleting)\b|\bi (?:request|wish|ask)\b|\bsolicito\b|\bpuede\w*\b|\bpueden\b|\bdemande\b|\bveuillez\b|\bmerci de\b|\bpor favor\b|\bbitte\b|依頼|요청|ください|주세요|\bwould like\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RequestVerbDeletion = new(@"\b(?:handle|clean\w*|finish|complete|investigat\w*|disable|switch off|stop|remove|delete|erase|ensure|make sure|inspect|verify|check|confirm\w*|audit|comprobar|completar)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Procedure = new(@"\b(?:deletion|erasure|removal)\s+(?:request|procedure|process|policy|requirements?)\b|\bprivacy process\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StatementHead = new(@"\bi(?:'m| am) (?:deleting|closing)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Filler = new(@"^\s*(?:and then|and|then|also|after that|afterwards|finally|next|plus|so)\b[,\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?。！？])\s+|\n+", RegexOptions.CultureInvariant);
    private static readonly Regex ClauseSplit = new(@",\s+(?=(?:but|so|and|however|although)\b)|;\s+|\s+(?:but|however|although)\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Imperative = new(@"^\s*(?:delete|erase|remove|close|wipe|purge|cancel|stop|send|keep|leave|do not|don't|handle|clean|finish|disable|replace|correct|change|update|tell|give|provide|check|verify|inspect|audit|investigate|confirm|unsubscribe)\b", RegexOptions.CultureInvariant);
    private static readonly Regex AccountWords = new(@"account|profile|cuenta|compte|conta|konto|\bit\b|\bthis\b", RegexOptions.CultureInvariant);
    private static readonly Regex AccountOrProfile = new(@"\baccount\b|\bprofile\b", RegexOptions.CultureInvariant);
    private static readonly Regex AccountProfilePersonal = new(@"\baccount\b|\bprofile\b|\bpersonal\b", RegexOptions.CultureInvariant);
    private static readonly Regex AuditWords = new(@"\b(?:verify|check|inspect|audit|investigat\w*|confirm\w*)\b|\bfinish the deletion\b", RegexOptions.CultureInvariant);
    private static readonly Regex TheDeletion = new(@"\b(?:the deletion|deletion as)\b", RegexOptions.CultureInvariant);
    private static readonly Regex VerifyWords = new(@"\b(?:verify|check|confirm|inspect|audit)\b", RegexOptions.CultureInvariant);
    private static readonly Regex DeleteMyThe = new(@"\b(?:delete|erase|remove)\s+(?:my|the|this|that|any|all)\b", RegexOptions.CultureInvariant);
    private static readonly Regex KeepAccount = new(@"\baccount\b|\bprofile\b|\bit as it is\b|\bin place\b", RegexOptions.CultureInvariant);
    private static readonly Regex KeepAsk = new(@"\bplease\b|\bdo not\b|\bdon't\b|\bleave\b|\bkeep\b", RegexOptions.CultureInvariant);
    private static readonly Regex NotClose = new(@"\bnot\b.*\bclos\w*|\bclos\w*.*\bnot\b|\bdo not close\b|\bdon't close\b", RegexOptions.CultureInvariant);
    private static readonly Regex StillUse = new(@"\bstill (?:use|need|want|have)\b|\bcontinue(?:d)? (?:to )?us\w*\b|\bwant to continue\b", RegexOptions.CultureInvariant);

    public static List<string> SplitClauses(string text)
    {
        var output = new List<string>();
        foreach (var sentence in SentenceSplit.Split(text.Trim()))
            foreach (var part in ClauseSplit.Split(sentence))
                if (!string.IsNullOrWhiteSpace(part)) output.Add(part.Trim());
        return output;
    }

    private static bool Negated(string clause, Match m)
    {
        var before = clause[..m.Index];
        var window = string.Join(" ", Py.Words(before).TakeLast(5));
        if (NegBefore.IsMatch(window)) return true;
        if (NotAsking.IsMatch(clause)) return true;
        return NegAnywhereCjk.IsMatch(clause[(m.Index + m.Length)..]);
    }

    public static Clause LabelClause(string clause)
    {
        var c = clause.Trim();
        var low = c.ToLowerInvariant();
        while (Filler.IsMatch(low)) low = Filler.Replace(low, "", 1);                // "and after that delete my account" -> "delete my account"
        bool marked = RequestMark.IsMatch(c) || Imperative.IsMatch(low);
        // 1. a verb of deletion (or closing the account)?
        var m = Del.Match(c);
        if (!m.Success)
        {
            var cm = Close.Match(c);
            if (cm.Success && AccountWords.IsMatch(low)) m = cm;
        }
        var noun = !m.Success ? DelNoun.Match(c) : Match.Empty;
        bool hasMarketing = Marketing.IsMatch(c);
        if (noun.Success)
        {
            // "deletion" as a noun: a request only next to a request verb ("I am requesting deletion", "begin the deletion process")
            if (Negated(c, noun)) return new Clause(c, "NEGATED", "deletion refused");
            if (AuditWords.IsMatch(low) && TheDeletion.IsMatch(low)) return new Clause(c, "AUDIT", "asks to audit/finish a deletion");
            if (PastContext.IsMatch(c) && !Init.IsMatch(c)) return new Clause(c, "CONTEXT", "narration");
            if (Init.IsMatch(c) || !Py.IsAscii(c)) return new Clause(c, "DELETE", "requests deletion (noun form)");
            return new Clause(c, "SUPPORT", "refers to deletion requirements/procedure");
        }
        if (m.Success)
        {
            if (Negated(c, m))
            {
                // refusal of deletion: matters only when the refused object is the account/data (not backups, statistics, ...)
                if (OtherObj.IsMatch(c) && !AccountOrProfile.IsMatch(low)) return new Clause(c, "CONTEXT", "negated deletion of a secondary object");
                return new Clause(c, "NEGATED", "deletion refused");
            }
            if (hasMarketing && (Only.IsMatch(c) || !AccountProfilePersonal.IsMatch(low))) return new Clause(c, "NONDELETE", "removal limited to marketing communications");
            if (PastContext.IsMatch(c) && !RequestMark.IsMatch(c)) return new Clause(c, "CONTEXT", "past-tense narration");
            if (StatementHead.IsMatch(c) || marked || !Py.IsAscii(c))
            {
                if (VerifyWords.IsMatch(low) && !DeleteMyThe.IsMatch(low)) return new Clause(c, "AUDIT", "asks to check a deletion");
                if (OtherObj.IsMatch(c) && !AccountObj.IsMatch(OtherObj.Replace(c, ""))) return new Clause(c, "CONTEXT", "allowance about a secondary object");
                return new Clause(c, "DELETE", "asks to delete/close");
            }
            return new Clause(c, "CONTEXT", "deletion word without a request");
        }
        // 2. refusals without a deletion verb: keep / leave the account
        if (Keep.IsMatch(c) && KeepAccount.IsMatch(low) && KeepAsk.IsMatch(low))
            if (!OtherObj.IsMatch(c) || AccountOrProfile.IsMatch(low)) return new Clause(c, "NEGATED", "asks to keep the account");
        if (NotClose.IsMatch(low)) return new Clause(c, "NEGATED", "closure refused");
        if (StillUse.IsMatch(low)) return new Clause(c, "NONDELETE", "wants to keep using the service");
        // 3. audit-style requests about a deletion's outcome
        if (AuditVerb.IsMatch(c) && StateWord.IsMatch(c)) return new Clause(c, "AUDIT", "asks to verify the outcome of a deletion");
        // 4. non-deletion topics (only as an ask or an explicit problem statement)
        if ((hasMarketing && marked) || (Access.IsMatch(c) && marked) || Correct.IsMatch(c) || Login.IsMatch(c)) return new Clause(c, "NONDELETE", "access/export/correction/login/marketing");
        if (Procedure.IsMatch(c)) return new Clause(c, "SUPPORT", "refers to the deletion request/procedure");
        if (PastContext.IsMatch(c) || StateWord.IsMatch(c)) return new Clause(c, "CONTEXT", "narration");
        return new Clause(c, "NEUTRAL", "");
    }

    public static Classification Decide(List<Clause> clauses, string rawText)
    {
        var labels = clauses.Select(c => c.Label).ToList();
        var positive = clauses.Where(c => c.Label is "DELETE" or "AUDIT").ToList();
        var negated = clauses.Where(c => c.Label == "NEGATED").ToList();
        var cls = new Classification(Ambiguous) { Clauses = clauses };
        if (positive.Count > 0 && negated.Count > 0)
        {
            cls.Reasons.Add("contradiction: both a deletion request and a refusal");
            return cls;                                              // AMBIGUOUS
        }
        if (negated.Count > 0)
        {
            cls.Label = NotApplicable;
            cls.Reasons.Add("deletion explicitly refused");
            return cls;
        }
        if (positive.Count > 0)
        {
            cls.Label = Deletion;
            cls.Reasons.Add($"{positive[0].Label.ToLowerInvariant()} clause: {positive[0].Why}");
            return cls;
        }
        bool hasContext = labels.Any(l => l is "CONTEXT" or "SUPPORT");
        bool asksDeletionAction = RequestVerbDeletion.IsMatch(rawText) && RequestMark.IsMatch(rawText);
        if (hasContext && clauses.Any(c => c.Label == "CONTEXT" && (StateWord.IsMatch(c.Text) || PastContext.IsMatch(c.Text))) && asksDeletionAction && !labels.Contains("NONDELETE"))
        {
            cls.Label = Deletion;
            cls.Reasons.Add("deletion narrated + request to act on it");
            return cls;
        }
        if (labels.Contains("SUPPORT") && !labels.Contains("NONDELETE"))
        {
            cls.Reasons.Add("mentions the deletion request but never states the wish itself");
            return cls;                                              // AMBIGUOUS: resolved conservatively to NOT_APPLICABLE
        }
        if (labels.Contains("NONDELETE"))
        {
            cls.Label = NotApplicable;
            cls.Reasons.Add("only non-deletion asks");
            return cls;
        }
        cls.Reasons.Add("no recognisable intent");
        return cls;                                                  // AMBIGUOUS
    }

    /// <summary>Deterministic rules; an AMBIGUOUS result is never turned into an authorization (NOT_APPLICABLE, method "fallback").</summary>
    public static Classification Classify(string text)
    {
        var clauses = SplitClauses(text).Select(LabelClause).ToList();
        var result = Decide(clauses, text);
        if (result.Label != Ambiguous) return result;
        result.Label = NotApplicable;
        result.Method = "fallback";
        result.Reasons.Add("ambiguous and unresolved: not authorizing deletion (conservative)");
        return result;
    }
}
