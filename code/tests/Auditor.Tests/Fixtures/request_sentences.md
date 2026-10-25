# Request sentence audit (all 112 distinct sentences in the 518 + 32 request texts)

Generated while preparing the Phase 2 classifier. Class meanings:

* **DELETE** - authorizes deletion/closure (or orders a concrete removal) of the account or its data
* **AUDIT** - asks to verify/inspect a deletion that was supposed to happen (gold treats these as deletion audits)
* **CONTEXT** - describes the situation, no request of its own
* **ALLOW** - a retention allowance inside a deletion request ("you may keep backups ...")
* **SCOPE** - scope/instruction modifier that only makes sense next to a deletion request
* **COURTESY** - confirmation / contact boilerplate
* **NONDELETE** - access, export, correction, update, login, marketing opt-out, or an explicit refusal of deletion

Request-level rule used by the classifier: a request is a deletion audit iff it contains at least one un-negated DELETE or AUDIT clause; otherwise NOT_APPLICABLE.

The classes below are my manual reading of the sentences; `ClassifyTests` checks the classifier against them (sentence level and request level).

## COURTESY (13)

| # | count | sentence | note |
|---|---|---|---|
| 1 | 66 | Please contact me if you need information to verify the request. |  |
| 2 | 66 | Please send a confirmation after the process finishes. |  |
| 3 | 63 | Let me know when the request has been completed. |  |
| 4 | 56 | A short confirmation when the process is complete is sufficient. |  |
| 6 | 52 | Please let me know once the request has been reviewed. |  |
| 7 | 51 | I would appreciate confirmation of the final outcome. |  |
| 8 | 51 | Please confirm when the review is complete. |  |
| 10 | 48 | Please confirm the result after completing the required checks. |  |
| 33 | 14 | Por favor, confirmem o resultado quando a análise terminar. |  |
| 37 | 13 | Merci de confirmer le résultat une fois la vérification terminée. |  |
| 41 | 13 | 確認が完了したら結果をお知らせください。 |  |
| 42 | 12 | Por favor, confirmen el resultado cuando termine la revisión. |  |
| 46 | 12 | 검토가 완료되면 결과를 확인해 주세요. |  |

## SCOPE (16)

| # | count | sentence | note |
|---|---|---|---|
| 13 | 46 | Please review any identifiers connected to this account as part of the request. |  |
| 14 | 45 | Please check the records connected to the account before confirming completion. |  |
| 15 | 44 | Please review the account data that is eligible for deletion or de-identification. |  |
| 16 | 43 | Please follow the applicable retention and deletion requirements for the account. |  |
| 17 | 41 | Please apply the request across the systems covered by your deletion policy. |  |
| 19 | 39 | Please ensure the request is applied to the relevant account records. |  |
| 20 | 38 | Please apply the correct treatment to records covered by the request. |  |
| 23 | 35 | Please include information linked through current or earlier account identifiers. |  |
| 25 | 31 | Please complete the normal identity and data review for this request. |  |
| 26 | 31 | Please handle any copies that fall within the scope of the deletion request. |  |
| 31 | 27 | Please include records associated with the account under your privacy process. |  |
| 35 | 14 | Verifiquem os identificadores atuais e anteriores conforme o processo de privacidade. |  |
| 38 | 13 | Veuillez examiner les identifiants actuels et antérieurs conformément à votre procédure de confidentialité. |  |
| 40 | 13 | プライバシー手続きに従い、現在および過去の識別情報を確認してください。 |  |
| 43 | 12 | Revisen los identificadores actuales y anteriores según su política de privacidad. |  |
| 45 | 12 | 개인정보 처리 절차에 따라 현재 및 이전 식별자를 검토해 주세요. |  |

## DELETE (36)

| # | count | sentence | note |
|---|---|---|---|
| 5 | 54 | Please treat this message as a request for permanent account deletion. |  |
| 9 | 49 | I would like to submit a request to delete this account and its personal data. |  |
| 11 | 46 | I am requesting deletion of the account registered to this email address. |  |
| 12 | 46 | Please erase the account linked to this email address. |  |
| 18 | 40 | I am asking you to remove my account and the personal information linked to it. |  |
| 21 | 38 | Please permanently delete the account associated with this email address. |  |
| 22 | 37 | I no longer use this account and would like it permanently deleted. |  |
| 24 | 33 | Please process this under the standard personal-data deletion procedure. | names the deletion procedure; otherwise generic |
| 27 | 31 | Please process a permanent account-deletion request for this email address. |  |
| 28 | 30 | I would like the account associated with this address to be erased. |  |
| 29 | 30 | Please close my account and process the associated personal-data deletion. |  |
| 30 | 29 | Please begin the deletion process for the account using this email address. |  |
| 32 | 23 | I want to close this account and have its associated personal data deleted. |  |
| 34 | 14 | Solicito a exclusão permanente da conta associada a este e-mail. |  |
| 36 | 13 | Je demande la suppression définitive du compte associé à cette adresse e-mail. |  |
| 39 | 13 | このメールアドレスに関連するアカウントの完全な削除を依頼します。 |  |
| 44 | 12 | Solicito la eliminación permanente de la cuenta asociada a este correo. |  |
| 47 | 12 | 이 이메일 주소에 연결된 계정의 영구 삭제를 요청합니다. |  |
| 50 | 1 | Can you remove that search result too? |  |
| 53 | 1 | Close the account completely and make sure it can no longer send my data to other systems. | close + stop sending data = delete + disable flows |
| 57 | 1 | Hi, I haven’t used this account in years and would like it closed. | closure verb ("closed"), no deletion word; deletion sample answers treat it as an audit |
| 73 | 1 | I want to close this account permanently. | "close permanently" = closure of the account |
| 79 | 1 | I’m deleting my account. | announces the deletion ("I'm deleting") |
| 86 | 1 | Please delete my account and the identity document I sent to support while resolving a delivery issue. |  |
| 87 | 1 | Please delete that export file as well. |  |
| 88 | 1 | Please delete this account, including the downloadable archive I requested earlier and any search result that still points to me. |  |
| 89 | 1 | Please handle them and disable any synchronization that could recreate them. | imperative "handle them and disable sync" - refers to traces of a deleted account |
| 91 | 1 | Please investigate and finish the deletion. | "finish the deletion" |
| 92 | 1 | Please keep only the reference needed for retained records and remove the rest. | redact tombstone, keep reference |
| 95 | 1 | Please remove any remaining copy of my profile. |  |
| 97 | 1 | Please remove my name and contact details, but keeping an internal deleted-account reference is fine if other required records still depend on it. | remove name/contact, keep internal reference (retention allowance inside) |
| 98 | 1 | Please remove my profile and clean my personal details out of earlier support conversations. |  |
| 99 | 1 | Please remove records linked to both the old and current addresses. |  |
| 100 | 1 | Please remove records under both addresses and switch off any integration that could copy them back into search or analytics. |  |
| 101 | 1 | Please remove the profile and the personal details attached to it. |  |
| 112 | 1 | ¿Pueden comprobar si todavía queda algún dato y completar la eliminación si hace falta? | asks to complete the deletion if needed (es) |

## AUDIT (7)

| # | count | sentence | note |
|---|---|---|---|
| 52 | 1 | Can you verify that my account was deleted? | verify deletion = audit |
| 55 | 1 | Could you verify that the profile is gone and that anything retained for security is limited to what is required? | verify deletion + retention = audit |
| 72 | 1 | I want confirmation that the account was fully removed at that point. | audit as of a past point |
| 82 | 1 | Please audit the deletion as it stood when my request was reviewed. | audit as of a past point |
| 84 | 1 | Please check that nothing tied to the old address was left behind, even in systems that do not display the email. | old-address leftovers |
| 85 | 1 | Please check the latest state and confirm that the profile is gone and that remaining records cannot identify me. | confirm profile gone |
| 90 | 1 | Please inspect the actual systems and tell me whether the account and identifiable analytics data have already been removed. | asks whether data "have been removed" - audit of a deletion (access-looking but about deletion state) |

## CONTEXT (16)

| # | count | sentence | note |
|---|---|---|---|
| 48 | 1 | A search result with a similar email belongs to someone else, so please do not treat that record as mine. | decoy-warning: a similar email belongs to someone else |
| 59 | 1 | I changed the email on this account before deleting it. |  |
| 60 | 1 | I closed my account, but an old “download your data” link still works. | closed account + lingering export link |
| 61 | 1 | I deleted my account and received the confirmation email. |  |
| 62 | 1 | I deleted this account and received a confirmation, but the same email address is still recognized. |  |
| 63 | 1 | I disconnected my account last week, but one of the integrations still seems active. | integration still active |
| 64 | 1 | I found an old report saying my account still existed. |  |
| 66 | 1 | I no longer need this account. | weak: no longer needs account, no verb of removal |
| 70 | 1 | I used a different email on this account before changing it. |  |
| 71 | 1 | I used this Yahoo address before changing the email on my account. |  |
| 75 | 1 | I was told that deletion was complete, but the account and its search result can still be found. |  |
| 76 | 1 | I was told the account had been deleted, but I can still find traces in search, support, and analytics. |  |
| 78 | 1 | Intenté eliminar mi cuenta ayer, pero la página terminó mostrando un error. | attempted deletion, error |
| 80 | 1 | My account page is gone, but my old profile still appears when I search for it. |  |
| 106 | 1 | The account itself is gone, but an internal deleted-account record still shows my personal details. |  |
| 108 | 1 | The privacy page still says my request failed. |  |

## ALLOW (4)

| # | count | sentence | note |
|---|---|---|---|
| 104 | 1 | Security records can stay if policy requires them. | retention allowance inside a deletion request |
| 105 | 1 | Some of those messages also relate to another customer, so the conversation itself may need to stay. | retention allowance (conversation must stay) |
| 110 | 1 | You can keep anonymous statistics, but nothing in analytics should still point back to me. | retention allowance (anonymous stats) |
| 111 | 1 | You may retain backups if required, but they must not be able to restore my personal profile. | retention allowance (backups, but restore blocked) |

## NONDELETE (20)

| # | count | sentence | note |
|---|---|---|---|
| 49 | 1 | Can you correct it without removing any of my records? | "removing" is NEGATED ("without removing") -> correction only |
| 51 | 1 | Can you tell me what personal information you hold about me? | ACCESS request (what do you hold?) - not an audit of a deletion |
| 54 | 1 | Could you send me a copy of the information held on my account? | ACCESS/copy request |
| 56 | 1 | Do not close the account. | closure negated |
| 58 | 1 | I can't access my profile and need help resetting the login. |  |
| 65 | 1 | I need a copy of last year's invoices for my records. |  |
| 67 | 1 | I still need the account and my order history, so I am not asking for deletion. | "not asking for deletion" explicit negation |
| 68 | 1 | I still use it, so please don't delete or change anything. | "don't delete or change anything" |
| 69 | 1 | I still want receipts and service messages for this account. |  |
| 74 | 1 | I want to continue using the service. |  |
| 77 | 1 | I'd like to download my account information in a portable format. | EXPORT request |
| 81 | 1 | My surname is misspelled on the account. |  |
| 83 | 1 | Please change the email address used for this account. |  |
| 93 | 1 | Please keep the account as it is. | keep account = deletion refused |
| 94 | 1 | Please leave my profile and purchase history in place. | leave in place = deletion refused |
| 96 | 1 | Please remove me from marketing lists only. | "remove me from marketing lists ONLY": remove-verb scoped to marketing communications, not the account |
| 102 | 1 | Please replace it with the new one and leave the account active. |  |
| 103 | 1 | Please stop the promotional emails. | stop promotional email = opt-out |
| 107 | 1 | The mobile number on my profile is out of date. |  |
| 109 | 1 | This is an information request, not a request to erase it. | "not a request to erase it" |

