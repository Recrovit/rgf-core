# Historical live-test infrastructure verification — 2026-10-22

> Historical evidence: the results below were captured before the move from rgf-demo to rgf-core. They do not verify the relocated project or establish its current live-test status. All Demo source and capture paths are relative to the rgf-demo checkout.

The live-test infrastructure works with the real configured OpenAI provider. The original Entity/Property list defect is proven and remains unresolved. A business-instruction-only candidate also failed live checks and was withdrawn; no production Core or Recrovit.AI workaround remains.

| Check | Result |
| --- | --- |
| Full test-project build, Debug, sequential local project references | Passed, 0 warnings and 0 errors |
| Final test-only Debug rebuild with existing local references | Passed, 0 warnings and 0 errors |
| Deterministic configuration and callback checks | 22 passed, 0 failed |
| Opt-in disabled | All live cases skipped; no login, network request or LLM call |
| Actual Duende authorization code + PKCE using current BlazorApp confidential client | Passed |
| Actual protected WebAPI provider/model catalog using the resulting user token | Passed; authentication test 1/1 |
| Real-provider LLM smoke | Passed, 1/1; exact answer, normalized usage, configured model and matching native HTTP attempts verified |
| Original Entity scenarios before the fix | 4 tests: 1 chat control passed, 3 failed; two-turn and direct enumeration reproduced count/heading-only responses, Property baseline exposed a diagnostic naming mismatch |
| Full live-enabled run after targeted instruction clarification | 28 total: 25 passed, 3 failed, 0 skipped; includes 22 deterministic cases and 6 live cases |
| Separate final Property follow-up live case | Passed, 1/1; history, fresh native read and explicit primary-key description verified |
| Final unmodified RGF Core regression suite | 259/259 passed in Debug after withdrawing the unsuccessful candidate |

Configuration is Development, using `JwtBearerOptions:Provider=Duende` and the WebAPI's `AddUserSecrets` path. The test process also reads the BlazorApp's selected Duende OIDC settings. Provider/model selection is taken from the current WebAPI, with no test-side model override. The default is OpenAI, alias `Luna-6`, model `gpt-6-luna`, default reasoning `low`. Native HTTP requests from the successful smoke and failed metadata scenarios confirm `model=gpt-6-luna` on the OpenAI Responses route.

The resolved WebAPI dependency manifest identifies local project dependencies: Legacy `RecroGrid/10.4.0`, RGF Core and Abstraction `10.3.0`, and Recrovit.AI Core/Runtime `10.0.0`. Actual SDK dependencies include MAF `1.25.0`, MEAI `10.10.0` and OpenAI SDK `2.14.0`. All build/test commands used Debug and ran sequentially.

The first authentication-only attempt reached the backend catalog stage after login but failed because the WebAPI was unavailable. A local API startup attempt failed at LocalDB initialization. After the developer started the existing HTTPS application, the same authentication-only test passed. No substitute database or identity principal was introduced.

A later full rebuild while Visual Studio hosted the API hit assembly file locks. The completed local references were then reused with `-p:BuildProjectReferences=false` for the final test-only Debug build; the running API was not stopped or modified. Build/test commands ran sequentially.

The authentication-only run was explicitly filtered to `RecrobyLiveAuthenticationTests`; it performs no Recroby POST, metadata disclosure to a provider or Credit-consuming request. The shared fixture keeps the user token in memory. The earlier ad hoc login helper and encrypted token file were removed; the implemented tests do not rely on them.

The developer explicitly authorized metadata/conversation disclosure to OpenAI with normal Credit consumption before the successful smoke and metadata reproduction. The previous automatic approval rejection was not bypassed; the tests were retried only after that approval.

## Proven original failure

All local captures are under `src/RGFDemo.WebAPI/logs/recroby-live/`, excluded from Git. The original first request is `7f291b485b9f2b5d8d1ebe7d4be4591f54f59280f6b7286dd49e359d51add643.jsonl`, workflow run `d206e7e5e670422e8d544216ed625e54`. Its follow-up is `8f283b126946a1b1c8cef87145294e9fd499aee0b531489666b036b3c5ccec8f.jsonl`, run `c37d1ddc9a6342ff8835f73ce111c5df`.

The actual authenticated catalog contains these application Entities:

| EntityId | EntityName |
| --- | --- |
| 10001 | RgCategory |
| 10002 | RgCustomer |
| 10003 | RgCustomerCustomerDemo |
| 10004 | RgCustomerDemographic |
| 10005 | RgEmployee |
| 10006 | RgEmployeeTerritory |
| 10007 | RgOrder |
| 10008 | RgOrderDetail |
| 10009 | RgProduct |
| 10010 | RgRegion |
| 10011 | RgShipper |
| 10012 | RgSupplier |
| 10013 | RgTerritory |

Intent selected `rgf.recroby.application`. The native model called `rgf_recroby_metadata_catalog_search` with `search=null, offset=0, pageSize=100`, call ID `call_xbKXf8IcugqpYEb8hkJUxNgJ`. Its real tool result contains all 13 exact names, `totalCount=13` and `nextOffset=null`; the next normalized provider request carries that result under the same call ID.

Both application HTTP requests contain a native strict `json_schema` with required `userMessage` and `output`, alongside both metadata functions. The actual catalog function executes and its result reaches the subsequent request while the final schema remains present. This reproduction therefore does not show an unsupported tool/structured-output combination on the selected OpenAI model.

Final provider invocation `bb2867ec8aeb446eab685180d6b57a49`, native HTTP attempt `92d253121d8b46678e69a5cb363be720`, returned `userMessage="Az alkalmazásban 13 Entity található:"` and a separate `output` string listing all 13 Entities. Native HTTP response, normalized response and deserialized agent completion agree. The HTTP response message equals the short `userMessage`. The follow-up and direct enumeration exhibit the same split. Names were not lost in the Metadata Reader, native tool loop, runtime mapping or client rendering.

The attempted Recroby business instruction requested a complete self-contained user answer including all requested Entity/Property names and rejected a heading/count as a substitute for a list. It mentioned no protocol fields and changed no schema, mapping or tool loop. A temporary deterministic suite passed 260 cases, but the live recheck still failed. That source change, its provisional deterministic test/fixture additions and its proposed release note were withdrawn. The final Core checkout has no changes from this investigation and passes its original 259 cases. Passing deterministic checks was not used to classify the failed live attempt as successful.

The diagnostic Customer probe initially searched for `Customer` only, while this demo's actual technical name is `RgCustomer`; the probe now accepts that known demo name. Actual Property names include `CustomerId`, not `CustomerID`. The tests retain complete-list assertions and compare exact case. The captured detail result has no dedicated typed primary-key marker, but the actual `CustomerId.propertyComment` explicitly states `Primary key that uniquely identifies each customer record.` The final follow-up test checks this authorized metadata description, the correct technical name, prior user history and a fresh native detail read. It is independent of the still-failing Property-enumeration assertion.

## Live recheck and remaining defect

The developer restarted the freshly built Debug WebAPI in Visual Studio. The native requests confirm that the candidate instruction reached the provider. The second full run produced these independent results:

| Scenario | Result and local evidence file |
| --- | --- |
| Original first turn and same-conversation follow-up | Both heading-only; `f6c80931baa59879362547f41150779cf06d5130ca1f25b72ef7547ce12edd29.jsonl`, `960b6cc62a253596d3d9cd100d18d7e2b2be118dbd1181e507486bc07cb8a1c9.jsonl` |
| Direct complete Entity list | Failed, all 13 names absent from displayed message; `ca8df80d05056ea4f711c5afedc181a1b74f0b6e2088f7240a704c172224a75f.jsonl` |
| Customer Property list | Failed, all 11 names absent from displayed message; `0a8b444b64d58b8d0c1a4476ca1e84dc5d58ef1301cb927474a117ae3c3b7350.jsonl` |
| Primary-key follow-up | Correct `CustomerId`, fresh detail tool call and explicit key comment; `b244ca29c729de834b0bc839b0b28dd5fd23960cde08d356d868e2e99bdc648d.jsonl` |
| General chat control | Passed, displayed `15`; `20b89b168b3a7716cfe2eaf8a2b339ccb8a5e6d8c774381eaad1026f16b5373b.jsonl` |
| Real provider smoke | Passed; `530db99171c05b37af3582f3b4a1aa57a64843165f25e72319b60205f9c338ef.jsonl` |

The follow-up was then separated into its own independently reported case and passed 1/1: setup trace `42358b85d79b49cdbfb81613e58138b6cd6416998466b0f0cffbd389988d72fe.jsonl`, follow-up `faa733c535b54891335c31e146e8e7fb205c8540ab79d8c7ee9d9cb06235bf01.jsonl`. The final project has 22 deterministic cases and 7 opt-in live cases. The first setup reply in the follow-up case establishes conversation context; full Property-list completeness remains checked by its separate failing test.

Entity/Property list failures were stable in the original run and one bounded candidate recheck. No additional model was substituted, assertions were not reduced to HTTP success/nonempty text, and no extra tool loop or JSON parsing fallback was introduced. The running developer-hosted process may still hold the unsuccessful candidate instruction in memory until its next normal rebuild/restart; the source change has been withdrawn and that process's results are explicitly reported as candidate evidence.

## Proposed contract decision before a production fix

The default Recroby terminal contract currently offers two independent text values: a domain string and a final user message. Native shape validation permits a nonempty introductory user message plus a complete domain list. The shared runtime then correctly projects that user message. More business-only prompt wording did not establish complete displayed answers.

The recommended direction to evaluate is a single user-facing answer for the default text-only Recroby branches, using the existing empty domain-output type under a separate registered contract ID. The intent graph, metadata tools, optional native tool selection, native structured final response and Credit hooks would remain. This needs no new workflow, provider client, manual JSON handling or separate LLM loop.

That choice changes the default workflow's exposed domain-output contract and must be explicitly treated as an integration/migration decision for consumers inspecting agent outputs. Keep the old string contract available for existing custom definitions, preserve the current registered-workflow override behavior, leave the HTTP response DTO unchanged and do not change general Recrovit.AI final-message mapping. No such contract change is implemented here.

Validation of an approved fix must prove the default terminal native schema has no second text channel for the list, preserve custom workflows and routing/history/Credit tests, and rerun all real cases against the same `gpt-6-luna` model with complete technical-name assertions. A production-fix deterministic regression must accompany that actual contract implementation; the withdrawn prompt test is not retained as evidence of a fix.
