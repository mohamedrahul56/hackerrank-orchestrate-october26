"""Request classifier: does `request_text` authorize deletion (-> audit it) or not (-> NOT_APPLICABLE)?

Meaning-based, deterministic, no case ids, no sentence templates. The text is split into clauses; each clause is
labelled from small multilingual lexicons by *what is being asked*:

  DELETE    asks to delete / erase / remove / close the account or its data (not negated, not marketing-only)
  AUDIT     asks to verify / inspect / finish a deletion that was supposed to have happened
  SUPPORT   mentions "the deletion request/procedure" without stating the wish itself (only counts with no refusal)
  NEGATED   refuses deletion of the account/data ("do not delete", "keep my account", "not asking for deletion")
  NONDELETE access / export / correction / login / marketing opt-out
  CONTEXT   past-tense narration ("I deleted my account"), retention allowances ("you may keep backups")
  NEUTRAL   courtesy / scope boilerplate

Request level (see `decide`):
  * any DELETE or AUDIT clause and no NEGATED clause           -> DELETION   (mixed requests: explicit deletion wins)
  * a NEGATED clause about the account                         -> NOT_APPLICABLE (even if other clauses mention deletion words)
  * deletion CONTEXT + a deletion-oriented request verb        -> DELETION   ("I was told it was deleted... please handle them")
  * SUPPORT only                                               -> AMBIGUOUS (never authorizes on its own: injection-safe)
  * otherwise                                                  -> NOT_APPLICABLE; unreadable/contradictory -> AMBIGUOUS
AMBIGUOUS ends as NOT_APPLICABLE (never authorize a deletion we cannot read). The request text is data, never instructions,
and no model reads it.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field

DELETION, NOT_APPLICABLE, AMBIGUOUS = "DELETION", "NOT_APPLICABLE", "AMBIGUOUS"

# ----------------------------------------------------------------------------- lexicons (one entry per language family)
# words that mean "delete / erase / remove / close the account"
# deletion NOUNS ("deletion", "suppression", ...): they only express a request together with a request verb (INIT below)
DEL_NOUN = re.compile(r"\b(?:de-?identification|deletion|erasure|removal|suppression|eliminaci[óo]n|exclus[ãa]o|supresi[óo]n|l[öo]schung|cancellazione)\b|削除|삭제|消去", re.I)
INIT = re.compile(r"\brequest (?:for|of)\b|\b(?:begin|start|submit|process|initiate|file|requesting|i request|asking(?: for)?|solicit\w*|demande\w*|i want|i need|i'?d like|would like|exercise my right)\b|依頼|요청", re.I)
DEL = re.compile(
    r"\b(?:delet(?:e|ed|es|ing)\b|eras(?:e|ed|es|ing)\b|remov(?:e|ed|es|ing)\b|eliminat\w*|wip(?:e|ed|ing)\b|purg\w*|destroy\w*|de-?identify|anonymi[sz]\w*|"
    r"deactivat\w*|terminat\w*|"
    r"elimin\w*|borr\w*|supr\w*|"                                  # es
    r"supprim\w*|effac\w*|"                                        # fr
    r"exclu\w*|apag\w*|excluir|"                                   # pt
    r"l[öo]sch\w*|entfern\w*|"                                     # de
    r"cancell\w*|rimuov\w*|rimozione)\b|"
    r"退会|解約|제거|탈퇴|폐쇄|말소", re.I)
CLOSE = re.compile(r"\bclos(?:e|ed|ing|ure)\b|\bcerr\w*|\bferm\w*|\bcl[ôo]tur\w*|\bencerr\w*|\bschlie[ßs]\w*|\bchiud\w*", re.I)
ACCOUNT_OBJ = re.compile(
    r"\baccount\b|\bprofile\b|\bcuenta\b|\bcompte\b|\bconta\b|\bkonto\b|\bpersonal\b|\bpersonal[- ]data\b|\bdata\b|\bdetails\b|"
    r"\binformation\b|\brecords?\b|\beverything\b|\banything\b|\bit\b|\bthem\b|\bthis\b|\bthat\b|\bthe rest\b|\bcopy\b|\bcopies\b|"
    r"\bsearch result\b|\bexport\b|\bdatos\b|\bdonn[ée]es\b|\bdados\b|アカウント|계정|アカウント|アカウント|\bcorreo\b|\be-?mail address\b|"
    r"\bidentifiers?\b|\bname\b|\bcontact\b", re.I)
OTHER_OBJ = re.compile(
    r"\bbackups?\b|\bsecurity (?:records?|logs?)\b|\bsecurity\b|\bstatistics\b|\banalytics\b|\bretained\b|\breceipts?\b|\binvoices?\b|"
    r"\bconversation\b|\bdeleted-account reference\b|\binternal .*reference\b|\breference\b|\baudit\b", re.I)
KEEP = re.compile(r"\b(?:keep|leave|retain|preserve|maintain|stay|in place|as it is)\b|\bconserv\w*|\bmantener\b|\bgarder\b|\bmanter\b", re.I)
NEG_BEFORE = re.compile(
    r"(?:\bnot\b|\bnever\b|\bwithout\b|\bno\b|n't\b|\bcannot\b|\bnicht\b|\bkein\w*|\bohne\b|\bsin\b|\bsans\b|\bsem\b|\bnon\b|\bsenza\b|\bne\b|\bn[ãa]o\b|"
    r"\binstead of\b|\brather than\b)", re.I)
NEG_ANYWHERE_CJK = re.compile(r"ないで|しない|不要|ず、|마세요|말아|지 마|않")
NOT_ASKING = re.compile(r"\bnot (?:asking|requesting|a request|an? request)\b|\bnot (?:to|for) (?:delet|eras|remov)|\bno (?:deletion|erasure)\b", re.I)
MARKETING = re.compile(r"\bmarketing\b|\bpromotion\w*|\bnewsletter\w*|\bmailing lists?\b|\bsubscri\w*|\bopt[- ]?out\b|\bunsubscrib\w*|\bpromo\b|\bcampaign\w*|\badvertis\w*", re.I)
ONLY = re.compile(r"\bonly\b|\bsolely\b|\bjust\b|\bnur\b|\bsolo\b|\bseulement\b|\bapenas\b", re.I)
ACCESS = re.compile(
    r"\b(?:send me|give me|provide me|tell me what|what (?:personal )?(?:information|data)|copy of|download|export|portable|"
    r"information (?:held|you hold)|invoices?)\b", re.I)
CORRECT = re.compile(r"(?:^|\b(?:please|can you|could you|would you|to|and|then)\s+)(?:correct|fix|update|change|replace|edit|modify)\b|\bmisspell\w*|\bout of date\b", re.I)
LOGIN = re.compile(r"\b(?:log ?in|password|reset|can't access|cannot access|locked out)\b", re.I)
AUDIT_VERB = re.compile(
    r"\b(?:verify|verif\w*|check|inspect|investigat\w*|audit|confirm\w*|finish|complete|comprobar|v[ée]rifi\w*|examin\w*|"
    r"verific\w*|pr[üu]f\w*|controll\w*)\b", re.I)
STATE_WORD = re.compile(
    r"\b(?:deleted|removed|erased|gone|closed|left behind|remain\w*|still (?:exist\w*|appear\w*|found|active|recogni[sz]ed|works?|shows?|there)|"
    r"can still be found|traces?|deletion|erasure|eliminaci[óo]n|eliminar|suppression|exclus[ãa]o|had been|was complete)\b|"
    r"完全な削除|삭제", re.I)
PAST_CONTEXT = re.compile(
    r"\bi(?:'ve| have)?\s+(?:already\s+)?(?:delet|clos|remov|eras|disconnect|tried to delet|tried to clos)\w*|"
    r"\b(?:was|were|had been|has been|have been|is|are)\s+(?:\w+\s+)?(?:delet|remov|eras|clos)\w*|\b(?:is|are) gone\b|"
    r"\b(?:before|after)\s+(?:delet|clos|remov)\w*|\bintent[ée]\s+(?:elimin|borr)\w*|"
    r"\bwas told\b|\bi found an old\b|\bstill says\b", re.I)
REQUEST_MARK = re.compile(
    r"\bquiero\b|\bquisiera\b|\bnecesito\b|\bje veux\b|\bje souhaite\b|\bquero\b|\bgostaria\b|\bich m[öo]chte\b|\bvorrei\b|\bi'?d like\b|\bi need\b|\bneed help\b|\bplease\b|\bcan you\b|\bcould you\b|\bwould you\b|\bi(?:'d| would) like\b|\bi want\b|\bi(?:'m| am) (?:asking|requesting|deleting)\b|"
    r"\bi (?:request|wish|ask)\b|\bsolicito\b|\bpuede\w*\b|\bpueden\b|\bdemande\b|\bveuillez\b|\bmerci de\b|\bpor favor\b|\bbitte\b|"
    r"依頼|요청|ください|주세요|\bwould like\b", re.I)
REQUEST_VERB_DELETION = re.compile(
    r"\b(?:handle|clean\w*|finish|complete|investigat\w*|disable|switch off|stop|remove|delete|erase|ensure|make sure|inspect|verify|check|confirm\w*|"
    r"audit|comprobar|completar)\b", re.I)
PROCEDURE = re.compile(r"\b(?:deletion|erasure|removal)\s+(?:request|procedure|process|policy|requirements?)\b|\bprivacy process\b", re.I)
STATEMENT_HEAD = re.compile(r"\bi(?:'m| am) (?:deleting|closing)\b", re.I)   # "I'm deleting my account": announcing the act itself


@dataclass
class Clause:
    text: str
    label: str
    why: str = ""


@dataclass
class Classification:
    label: str                         # DELETION | NOT_APPLICABLE | AMBIGUOUS
    method: str = "rules"              # "rules" | "fallback" (ambiguous, resolved conservatively)
    clauses: list[Clause] = field(default_factory=list)
    reasons: list[str] = field(default_factory=list)


def split_clauses(text: str) -> list[str]:
    sentences = re.split(r"(?<=[.!?。！？])\s+|\n+", text.strip())
    out: list[str] = []
    for s in sentences:
        parts = re.split(r",\s+(?=(?:but|so|and|however|although)\b)|;\s+|\s+(?:but|however|although)\s+", s)
        out += [p.strip() for p in parts if p and p.strip()]
    return out


def _negated(clause: str, m: re.Match) -> bool:
    before = clause[: m.start()]
    window = " ".join(before.split()[-5:])
    if NEG_BEFORE.search(window):
        return True
    if NOT_ASKING.search(clause):
        return True
    if NEG_ANYWHERE_CJK.search(clause[m.end():]):
        return True
    return False


FILLER = re.compile(r"^\s*(?:and then|and|then|also|after that|afterwards|finally|next|plus|so)\b[,\s]*", re.I)


def label_clause(clause: str) -> Clause:
    c = clause.strip()
    low = c.lower()
    while FILLER.match(low):                                  # "and after that delete my account" -> "delete my account"
        low = FILLER.sub("", low, count=1)
    imperative = re.match(r"\s*(?:delete|erase|remove|close|wipe|purge|cancel|stop|send|keep|leave|do not|don't|handle|clean|finish|disable|replace|correct|change|update|tell|give|provide|check|verify|inspect|audit|investigate|confirm|unsubscribe)\b", low)
    marked = bool(REQUEST_MARK.search(c)) or bool(imperative)
    # 1. a verb of deletion (or closing the account)?
    m = DEL.search(c)
    if m is None:
        cm = CLOSE.search(c)
        if cm is not None and re.search(r"account|profile|cuenta|compte|conta|konto|\bit\b|\bthis\b", low):
            m = cm
    noun = DEL_NOUN.search(c) if m is None else None
    has_marketing = bool(MARKETING.search(c))
    if noun is not None:
        # "deletion" as a noun: a request only next to a request verb ("I am requesting deletion", "begin the deletion process")
        if _negated(c, noun):
            return Clause(c, "NEGATED", "deletion refused")
        if re.search(r"\b(?:verify|check|inspect|audit|investigat\w*|confirm\w*)\b|\bfinish the deletion\b", low) and re.search(r"\b(?:the deletion|deletion as)\b", low):
            return Clause(c, "AUDIT", "asks to audit/finish a deletion")
        if PAST_CONTEXT.search(c) and not INIT.search(c):
            return Clause(c, "CONTEXT", "narration")
        if INIT.search(c) or not c.isascii():
            return Clause(c, "DELETE", "requests deletion (noun form)")
        return Clause(c, "SUPPORT", "refers to deletion requirements/procedure")
    if m is not None:
        if _negated(c, m):
            # refusal of deletion: matters only when the refused object is the account/data (not backups, statistics, ...)
            if OTHER_OBJ.search(c) and not re.search(r"\baccount\b|\bprofile\b", low):
                return Clause(c, "CONTEXT", "negated deletion of a secondary object")
            return Clause(c, "NEGATED", "deletion refused")
        if has_marketing and (ONLY.search(c) or not re.search(r"\baccount\b|\bprofile\b|\bpersonal\b", low)):
            return Clause(c, "NONDELETE", "removal limited to marketing communications")
        if PAST_CONTEXT.search(c) and not REQUEST_MARK.search(c):
            return Clause(c, "CONTEXT", "past-tense narration")
        if STATEMENT_HEAD.search(c) or marked or not c.isascii():
            if re.search(r"\b(?:verify|check|confirm|inspect|audit)\b", low) and not re.search(r"\b(?:delete|erase|remove)\s+(?:my|the|this|that|any|all)\b", low):
                return Clause(c, "AUDIT", "asks to check a deletion")
            if OTHER_OBJ.search(c) and not ACCOUNT_OBJ.search(re.sub(OTHER_OBJ, "", c)):
                return Clause(c, "CONTEXT", "allowance about a secondary object")
            return Clause(c, "DELETE", "asks to delete/close")
        return Clause(c, "CONTEXT", "deletion word without a request")
    # 2. refusals without a deletion verb: keep / leave the account
    if KEEP.search(c) and re.search(r"\baccount\b|\bprofile\b|\bit as it is\b|\bin place\b", low) and \
            re.search(r"\bplease\b|\bdo not\b|\bdon't\b|\bleave\b|\bkeep\b", low):
        if not OTHER_OBJ.search(c) or re.search(r"\baccount\b|\bprofile\b", low):
            return Clause(c, "NEGATED", "asks to keep the account")
    if re.search(r"\bnot\b.*\bclos\w*|\bclos\w*.*\bnot\b|\bdo not close\b|\bdon't close\b", low):
        return Clause(c, "NEGATED", "closure refused")
    if re.search(r"\bstill (?:use|need|want|have)\b|\bcontinue(?:d)? (?:to )?us\w*\b|\bwant to continue\b", low):
        return Clause(c, "NONDELETE", "wants to keep using the service")
    # 3. audit-style requests about a deletion's outcome
    if AUDIT_VERB.search(c) and STATE_WORD.search(c):
        return Clause(c, "AUDIT", "asks to verify the outcome of a deletion")
    # 4. non-deletion topics (only as an ask or an explicit problem statement)
    if (has_marketing and marked) or (ACCESS.search(c) and marked) or CORRECT.search(c) or LOGIN.search(c):
        return Clause(c, "NONDELETE", "access/export/correction/login/marketing")
    if PROCEDURE.search(c):
        return Clause(c, "SUPPORT", "refers to the deletion request/procedure")
    if PAST_CONTEXT.search(c) or STATE_WORD.search(c):
        return Clause(c, "CONTEXT", "narration")
    return Clause(c, "NEUTRAL", "")


def decide(clauses: list[Clause], raw_text: str) -> Classification:
    labels = [c.label for c in clauses]
    positive = [c for c in clauses if c.label in ("DELETE", "AUDIT")]
    negated = [c for c in clauses if c.label == "NEGATED"]
    cls = Classification(AMBIGUOUS, clauses=clauses)
    if positive and negated:
        cls.reasons.append("contradiction: both a deletion request and a refusal")
        return cls                                              # AMBIGUOUS
    if negated:
        cls.label = NOT_APPLICABLE
        cls.reasons.append("deletion explicitly refused")
        return cls
    if positive:
        cls.label = DELETION
        cls.reasons.append(f"{positive[0].label.lower()} clause: {positive[0].why}")
        return cls
    has_context = any(l in ("CONTEXT", "SUPPORT") for l in labels)
    asks_deletion_action = bool(REQUEST_VERB_DELETION.search(raw_text)) and bool(REQUEST_MARK.search(raw_text))
    if has_context and any(c.label == "CONTEXT" and (STATE_WORD.search(c.text) or PAST_CONTEXT.search(c.text)) for c in clauses) \
            and asks_deletion_action and not any(l == "NONDELETE" for l in labels):
        cls.label = DELETION
        cls.reasons.append("deletion narrated + request to act on it")
        return cls
    if "SUPPORT" in labels and "NONDELETE" not in labels:
        cls.reasons.append("mentions the deletion request but never states the wish itself")
        return cls                                              # AMBIGUOUS: resolved conservatively to NOT_APPLICABLE
    if "NONDELETE" in labels:
        cls.label = NOT_APPLICABLE
        cls.reasons.append("only non-deletion asks")
        return cls
    cls.reasons.append("no recognisable intent")
    return cls                                                  # AMBIGUOUS


def classify_request(text: str) -> Classification:
    """Deterministic rules; an AMBIGUOUS result is never turned into an authorization (NOT_APPLICABLE, method "fallback")."""
    clauses = [label_clause(c) for c in split_clauses(text)]
    result = decide(clauses, text)
    if result.label != AMBIGUOUS:
        return result
    result.label, result.method = NOT_APPLICABLE, "fallback"
    result.reasons.append("ambiguous and unresolved: not authorizing deletion (conservative)")
    return result
