# Tác vụ xuất nhãn FBS và mua KIZ — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Execution method preserved: native, in this session.

**Goal:** Hợp nhất điều phối xuất nhãn FBS ba sàn, mua/phục hồi KIZ đúng ý định và bảo toàn mã sau lỗi, retry hoặc restart.

**Architecture:** Lớp coordinator C#/SQLite bao quanh adapter/journal hiện có. Tách purchase intent, inventory eligibility, request policy và UI subscription; remote state của từng sàn giữ riêng. Không thêm runtime Node/Go hoặc dependency sản phẩm mới.

**Tech Stack:** .NET 8 WinForms, Microsoft.Data.Sqlite, DPAPI, HttpClient, CryptoPro cryptcp hiện có, SkiaSharp/ZXing, console test suites và Windows GitHub Actions.

**Spec:** [2026-10-02-fbs-kiz-label-jobs-design.md](../specs/2026-10-02-fbs-kiz-label-jobs-design.md), người dùng duyệt ngày 02/10/2026. Snapshot spec commit `3100a34e8e281d32b7b36bc4aecbf3e583e4b317`; source app base `79de195ada1108bcf2837023ee683f77fddfa3d9`.

## Global Constraints

- App Windows độc lập; giữ `net8.0-windows` trong đợt này.
- Migrations chỉ bổ sung bảng/cột/index; không xóa hoặc tái cấp mã đã giữ.
- Quyền/license, scope cửa hàng và ranh giới Test Center hiện có tiếp tục được kiểm tra tại service boundary.
- Token, API key, omsConnection, private key và raw KIZ không xuất hiện trong log/báo cáo; raw code trong các bảng mới được DPAPI bảo vệ. Không đưa secret vào snapshot/hash đầu vào.
- Store deletion xóa dữ liệu thuộc store, dừng tác vụ thuộc store và không cho worker tái tạo store. Không gọi API hủy đơn mua từ xa chỉ vì xóa store.
- Remote ID, request key, payload hash và snapshot đã gửi là bất biến sau khi bắt đầu mutation.
- Không coi HTTP 2xx là bằng chứng hoàn tất tác vụ.
- Không tự nhận thêm đơn mới, tạo shipment WB mới hoặc giao shipment WB khi bấm Xuất nhãn.
- Installer vẫn bị khóa đến khi hoàn tất regression và vòng kiểm tra API thật đã thống nhất; bản thử là portable.

Các giá trị từ phần khác của spec: SafeRead tối đa **3 attempts**; mutation/StatefulRead **1 attempt** trước đối soát; quantity 0 không mua, quantity âm từ chối; scope mặc định không dùng chéo store; không thêm framework test/dependency runtime mới.

## Review Focus

- Proxy trả HTTP 200 với HTML/JSON thiếu ID hoặc error trong body: không báo mua thành công hoặc gửi lại; test thuộc Task 2/3.
- Scanner prefix/GS/FNC1 và crypto tail khác: giữ nguyên payload evidence, alias cùng CIS không thành hai unit, tail conflict phải đối soát; test thuộc Task 1/4.
- API response bị trễ trong lúc credential/store generation đổi: giữ remote ID để audit nhưng worker không cấp mã, ghi lại store đã xóa hoặc mở gate cũ; test thuộc Task 3/4/6.
- Nhu cầu reordered/mapping version đổi/quantity giảm khi job active: không tạo revision tự động hoặc thay mã unit đã có; test thuộc Task 4.
- Legal proof được cập nhật trước hoặc sau in/dán: hai prerequisites độc lập, legacy AVAILABLE chưa đủ proof, checkbox vật lý không mở legal gate; test thuộc Task 4/5.

## Cách thực thi và kiểm chứng

1. Dùng `superpowers:using-git-worktrees` tại thời điểm bắt đầu execution, tạo nhánh `feat/fbs-kiz-label-jobs` từ commit chứa plan đã duyệt. Kiểm tra HEAD/AGENTS/working tree mới trước sửa.
2. Giữ spec/plan trên nhánh docs đến khi người dùng duyệt plan. Không merge main hoặc ghi API seller trong bước planning.
3. Máy hiện tại chưa có `dotnet` trong PATH. Các lệnh dotnet dưới đây chạy trên Windows runner; không coi kiểm tra tĩnh là test đã qua.
4. Task 1 thêm nhánh feature vào workflow regression để cả commit RED và GREEN chạy trên Windows. Có thể push một commit test đỏ; mỗi task phải có bằng chứng đỏ đúng ca mới và xanh sau sửa.
5. Theo dõi workflow của đúng SHA: đọc conclusion và log từng suite. Timeout/failure/compile error ngoài ca đang tái hiện không được báo GREEN.
6. Sau mỗi task, commit hạng mục tự kiểm chứng được. Sau Task 6, whole-branch review theo `superpowers:requesting-code-review`, xử lý finding có bằng chứng và chạy lại suite bị ảnh hưởng.
7. Portable chỉ là bản thử đã kiểm tra bằng fixture. API thật/chi phí mua/máy in vẫn qua Test Center và bước seller thực hiện; installer giữ khóa.

## File map

Đường dẫn đều từ repository root; tên mới dưới đây là quyết định của plan, chưa tồn tại trong app.

| Nhóm | Files và trách nhiệm |
|---|---|
| Domain | `MarketplaceHub/Core/FbsLabelJobs.cs`: target/unit/snapshot/result; `SuzModels.cs`: profile, intent, block/outcome; `KizEligibility.cs`: scope, proof, allocation, physical evidence |
| Storage | `Infrastructure/AppDatabase.FbsLabelJobs.cs`, `AppDatabase.SuzPurchases.cs`, `AppDatabase.ScopedKiz.cs`: partial repositories; `KizCodeProtector.cs`: DPAPI/HMAC; `DatabaseInstanceLock.cs`: khóa cùng DB |
| SUZ | `Services/Suz/OperationPolicy.cs`, `SuzHttpClient.cs`, `CryptoProSuzSigner.cs`, `TrueApiKizReader.cs`, `KizPurchaseCoordinator.cs` |
| FBS | `Services/Fbs/FbsLabelJobCoordinator.cs`, `FbsLabelAdapters.cs`, `LabelArtifactStore.cs`, `KizEligibilityService.cs`; `Services/AppServices.FbsLabelJobs.cs`: entry point/DI |
| UI | `UI/FbsLabelJobDialog.cs`, `KizWorkflowProfileDialog.cs`, `MainForm.FbsLabelJobs.cs`; sửa entry point WB/non-WB và Test Center hiện có |
| Test | `tests/MarketplaceHub.Workflows/`: suite mới, các group persistence, transport, purchase, allocation, coordinator; test UI/print/license hiện có kiểm tra tương tác và hồi quy |
| Shared test support | `tests/MarketplaceHub.TestSupport/WorkflowTestSupport.cs`: signed fixture license, scoped code/profile fixture; chỉ link từ project test |
| Workflow/docs | `.github/workflows/marketplace-regression.yml`, `.github/workflows/build-marketplace-hub.yml`, README và ledger đợt này |

## Quy ước type dùng giữa các task

Tất cả model mới thuộc `MarketplaceHub.Core`; unit index bắt đầu từ 0. Hash là hex uppercase. Model chứa credentials/raw code không dùng auto-generated ToString đưa giá trị ra log. Các model/list/payload nêu dưới đây chỉ chứa metadata, raw code chỉ tồn tại trong transient transport/block hoặc DPAPI storage.

- `LabelTarget(long StoreId, Marketplace Marketplace, LabelTargetKind Kind, string TargetId)`; kind là WbSupply hoặc MarketplaceBatch.
- `FbsUnitKey(long StoreId, Marketplace Marketplace, string OrderId, string ItemId, int UnitIndex)`.
- `FbsUnitDemand(FbsUnitKey Unit, string Sku, string Gtin, long MappingVersion, bool RequiresKiz)`.
- `LabelJobSnapshot(LabelTarget Target, int StoreGeneration, IReadOnlyList<FbsUnitDemand> Units, string ItemFingerprint, string RequirementFingerprint)`.
- `FbsLabelJob`: Id:string, Revision:int, Snapshot:LabelJobSnapshot, SnapshotHash:string, Stage:FbsLabelJobStage, Active:bool, Version:long, UpdatedAt:DateTimeOffset.
- `UnitWorkflowResult(FbsUnitKey Unit, string Stage, string? CodeHash, string? ErrorCode)`.
- `LabelJobResult(string JobId, int Revision, FbsLabelJobStage Stage, int VerifiedUnits, int TotalUnits, IReadOnlyList<UnitWorkflowResult> Units, IReadOnlyList<LabelArtifact> Artifacts, string? ErrorCode)`.
- `SuzProfile`: Id:string, StoreId:long, Version:int, OwnerInn:string, Environment:string, ProductGroup:string, ReleaseMethodType:string, CisType:string, TemplateId:int, CredentialVersion:string, AutoPurchaseEnabled:bool, ContractEnabled:bool, SuzBaseUri:Uri, TrueApiBaseUri:Uri.
- `PurchaseIntentRequest(string JobId, int Revision, SuzProfile Profile, string Gtin, int Quantity, string PayloadHash)`.
- `PurchaseIntent`: Id:string, Request:PurchaseIntentRequest, RequestKey:string, Stage:PurchaseStage, RemoteOrderId:string?, RetryAt:DateTimeOffset?, ErrorCode:string?.
- `SuzOutcome<T>(SuzOutcomeKind Kind, T? Value, string? ErrorCode, DateTimeOffset? RetryAt)`; Kind = Confirmed, Rejected, Unknown, Pending, Unsupported.
- `SuzOrderReceipt(string OrderId)`, `SuzOrderStatus(string OrderId, string State, int AvailableCodes, string? RejectionCode)`, `SuzOrderCandidate(string OrderId, string OwnerInn, string Environment, string Gtin, int Quantity, string? CorrelationProof)`, `SuzBlock(string BlockId, string OrderId, string Gtin, IReadOnlyList<string> Codes)`.
- `KizScope(long StoreId, string OwnerInn, string Environment)`.
- `KizLegalProof(string CodeHash, string Gtin, string OwnerInn, string Environment, string RawStatus, string StatusEx, string PackageType, DateTimeOffset ObservedAt, string Source)`.
- `PhysicalMarkEvidence(FbsUnitKey Unit, string CodeHash, DateTimeOffset ConfirmedAt)`.
- `LabelArtifact(string Id, string JobId, int Revision, string Kind, string FilePath, string Sha256, IReadOnlyList<FbsUnitKey> Units, bool OfficialMarketplaceLabel)`.
- `IWorkflowClock`: `DateTimeOffset UtcNow { get; }`, `Task DelayAsync(TimeSpan delay, CancellationToken ct)`; production clock dùng UTC, test clock không sleep.
- Các enum stage giữ đúng tên trong spec: FBS Queued, Validating, AwaitingPurchase, Purchasing, AwaitingCodes, Recovering, AwaitingLegalState, AwaitingPhysicalMark, Attaching, Verifying, LabelsReady, Partial, Paused, NeedsReconciliation, SnapshotChanged, Failed, Cancelled; purchase Draft, Validated, CreateSending, CreateUnknown, Polling, Downloading, DownloadUnknown, Recovering, AwaitingLegalState, CodesRecovered, Rejected, NeedsReconciliation.
- `WorkflowRunMode`: UserRequested hoặc RecoveryOnly. RecoveryOnly không CreateOrder, ReceiveCodes mới, attach hoặc packing; chỉ đọc/poll/phục hồi block đã biết target/capability.
- Job snapshot hash sắp xếp theo Unit key ordinal; gồm mapping/requirement/GTIN và quantity dưới dạng unit expansion. Không đổi hash chỉ vì input list reordered; không đưa raw KIZ/token vào snapshot.
- Canonical raw-code fingerprint là HMAC-SHA256 bằng khóa DPAPI của DB; giữ cryptographic tail và case serial. Thêm CisHash riêng cho GTIN + serial để hai payload khác tail của cùng CIS không được cấp thành hai unit. Prefix/GS normalization theo parser có fixture, không tự xóa separator đang mang nghĩa phân tách AI.

---

### Task 1: Persistence và migration giữ đúng identity

**Files:**
- Create: ba database partial, ba Core model files, `KizCodeProtector.cs`, `DatabaseInstanceLock.cs`.
- Modify: `Infrastructure/AppDatabase.cs` (Initialize/DeleteStore/SaveZnakConfig), `MarketplaceHub/Program.cs`.
- Create: `tests/MarketplaceHub.Workflows/MarketplaceHub.Workflows.csproj`, `Program.cs`, `WorkflowTestRunner.cs`, `PersistenceTests.cs`, `WorkflowFixture.cs`; shared test support.
- Modify: `.github/workflows/marketplace-regression.yml`.

**Interfaces:**
- Consumes: existing `AppDatabase(string dbPath)`, `StoreProfile`, legacy pool/pipeline and reservation tables.
- Produces:
  - `FbsLabelJob GetOrCreateLabelJob(LabelJobSnapshot snapshot)`.
  - `FbsLabelJob? GetLabelJob(string jobId)`; `bool TryClaimLabelJob(string jobId, long expectedVersion)`; `void ReleaseLabelJob(string jobId)`.
  - `bool TrySaveLabelJob(string jobId, long expectedVersion, FbsLabelJobStage stage, IReadOnlyList<UnitWorkflowResult> units)` (optimistic concurrency).
  - `PurchaseIntent GetOrCreatePurchaseIntent(PurchaseIntentRequest request)`; `PurchaseIntent? GetPurchaseIntent(string id)`; `PurchaseIntent? FindOpenPurchase(KizScope scope, string gtin)`.
  - `bool TryBeginPurchase(string intentId)`; `void SavePurchaseOutcome(string intentId, PurchaseStage stage, string? remoteOrderId, DateTimeOffset? retryAt, string? errorCode)`; ID null không xóa ID đã có.
  - `void SavePurchaseBlock(string intentId, SuzBlock block)`; `IReadOnlyList<SuzBlock> PurchaseBlocks(string intentId)`.
  - `void SaveSuzProfile(SuzProfile profile)`; `SuzProfile? SuzProfileForStore(long storeId)`; `int StoreGeneration(long storeId)`; `bool StoreGenerationMatches(long storeId, int generation)`.
  - `DatabaseInstanceLock.TryAcquire(string dbPath, out DatabaseInstanceLock? heldLock)`; lock IDisposable.
  - `IKizCodeProtector.Protect(string raw)`, `Unprotect(string encrypted)`, `Identity(string raw)`, `CisIdentity(string raw)`, cả bốn trả string.
- Test support: `WorkflowFixture.Create()` tạo store WB, SQLite temp, default snapshot hai unit và profile auto-purchase tắt; properties Db/Store/Snapshot/Profile/DbPath; `AppDatabase WorkflowFixture.Reopen()` tạo AppDatabase mới cùng file. `WorkflowTestRunner.CheckAsync(string name, Func<Task> test)`, `Expect(bool condition, string reason)`, ExitCode khác 0 nếu fail. Program chọn group qua `--group persistence`.

- [ ] **Step 1: Viết test persistence đỏ**

Trong PersistenceTests, tên/điều kiện tối thiểu:

```csharp
// active_target_is_unique_and_reorder_is_stable
Expect(first.Id == second.Id && first.SnapshotHash == reordered.SnapshotHash,
       "Repeated/reordered request must use the same active job");
// intent_remote_id_survives_error_and_restart
Expect(reopened.GetPurchaseIntent(intent.Id)!.RemoteOrderId == "SUZ-1",
       "ERROR update or restart lost remote order ID");
// legacy_error_is_not_rebuyable
Expect(importedKnown.Stage == PurchaseStage.Polling &&
       importedUnknown.Stage == PurchaseStage.NeedsReconciliation,
       "Legacy error must resume or reconcile, never become a new purchase");
// canonical_alias_has_one_binding
Expect(firstIdentity == prefixedIdentity && firstIdentity != differentTailIdentity,
       "Scanner aliases or cryptographic tail handled incorrectly");
// instance_and_deleted_store_are_fenced
Expect(!secondInstanceAcquired && !db.StoreGenerationMatches(storeId, generation),
       "Concurrent instance/deleted-store worker was allowed");
// protected_new_tables_do_not_contain_raw_code
Expect(!dumpOfNewTables.Contains(rawCode, StringComparison.Ordinal),
       "New persistence leaked raw KIZ");
```

Arrange bằng fixture nói trên; test SavePurchaseOutcome(Polling,"SUZ-1"), rồi SavePurchaseOutcome(NeedsReconciliation,null), mở lại DB. Dùng raw test code chứa GS và prefix scanner; differentTail giữ cùng GTIN/serial nhưng khác tail. Seed legacy ERROR cả có ID lẫn không ID bằng API SQLite hiện có. Test quantity âm và thay payload/quantity của intent đã có phải bị từ chối, không silently replace.

- [ ] **Step 2: Chạy RED trên Windows**

Run: `dotnet run --project tests/MarketplaceHub.Workflows -c Release -- --group persistence`.
Expected: nonzero exit do interface/type mới chưa tồn tại hoặc assertion mới thất bại; log ghi tên test và lý do. Thêm project `net8.0-windows`, ProjectReference app, không NuGet test framework. Workflow thêm `feat/fbs-kiz-label-jobs` vào push branches và step suite workflows; giữ các suite cũ. RED compile chỉ được chấp nhận khi lỗi đúng interface đang được task tạo, không lỗi unrelated.

- [ ] **Step 3: Implement repository/lock/model đúng contract**

Tạo additive tables: fbs_label_jobs, fbs_job_units, kiz_purchase_intents, kiz_purchase_blocks, kiz_codes_scoped, kiz_unit_bindings, kiz_profiles, workflow_store_generations, kiz_crypto_metadata. Unique active target bằng filtered index active=1; intent unique job/revision/profile/environment/GTIN; code identity/CisHash toàn DB; binding active một unit/một CIS. Tail conflict giữ evidence block, không overwrite raw code cũ hoặc tăng tồn. Mỗi row lưu source/version/checkpoint cần thiết.

GetOrCreate dùng transaction BEGIN IMMEDIATE và đọc lại row thắng race; snapshot changed trả job cũ với SnapshotChanged, không tự tạo revision. Claim/version update không giữ transaction qua network await. Persist block/code atomically, DPAPI CurrentUser cho khóa HMAC và raw code mới.

Migration idempotent: pipeline có remote ID giữ ID và resume, stage bất định không ID vào NeedsReconciliation; duplicate legacy reference cùng order/GTIN được liên kết cùng intent. Pool AVAILABLE legacy thành NeedsVerification, không tự xác nhận legal/owner. Không sửa/xóa legacy raw rows hoặc reservations.

Store delete tăng generation trước khi xóa scoped jobs/profile/code thuộc store; late Save có generation cũ trả false. Credential config save tăng version khi config thay đổi; không dùng hash chứa secret để thay version. Profile/version cũ giữ snapshot business; metadata authorization version lưu riêng, không overwrite RequestProfile. Khóa instance bằng FileStream FileShare.None cạnh DB, held suốt vòng Application.Run, kernel nhả sau crash; self-test giữ temp DB riêng.

- [ ] **Step 4: Chạy GREEN**

Run lại group persistence và `dotnet run --project tests/MarketplaceHub.FbsState -c Release`.
Expected: zero failures; mở DB hai lần migration không nhân đôi intent/code; existing reservation rows và remote IDs không đổi. Nếu old fixture cần signed license/scoped proof, dùng shared support, giữ nguyên assertions nghiệp vụ.

- [ ] **Step 5: Commit**

Commit: `feat: persist FBS label jobs and KIZ purchase intents`. Ghi SHA/run ID/RED→GREEN vào `docs/superpowers/plans/2026-10-02-fbs-kiz-label-jobs-execution-status.md`.


### Task 2: SUZ transport có outcome và policy theo operation

**Files:**
- Create: `Services/Suz/OperationPolicy.cs`, `SuzHttpClient.cs`, `CryptoProSuzSigner.cs`, `TrueApiKizReader.cs`.
- Modify: `Services/AppServices.cs` (tách các helper auth/create/poll/download/sign, chưa đổi entry point FBS).
- Create: `tests/MarketplaceHub.Workflows/TransportTests.cs`, `SuzFixtureHttp.cs`, `FakeWorkflowClock.cs`; đăng ký group transport trong Program.

**Interfaces:**
- Consumes: Task 1 SuzProfile/outcome/model/clock. OperationKind gồm SuzCreate, SuzStatus, SuzListOrders, SuzListBlocks, SuzReceiveCodes, SuzRecoverBlock, TrueApiCisesInfo, SuzAuthChallenge, SuzSignIn, FbsMutation; Safety gồm SafeRead, StatefulRead, Mutation, Reconcile.
- Produces `ISuzClient` với các method:
  - `Task<SuzOutcome<SuzOrderReceipt>> CreateOrderAsync(PurchaseIntent intent, CancellationToken ct)`.
  - `Task<SuzOutcome<SuzOrderStatus>> ReadOrderAsync(PurchaseIntent intent, CancellationToken ct)`.
  - `Task<SuzOutcome<IReadOnlyList<SuzOrderCandidate>>> ListOrdersAsync(PurchaseIntent intent, CancellationToken ct)`.
  - `Task<SuzOutcome<SuzBlock>> ReceiveCodesAsync(PurchaseIntent intent, int quantity, CancellationToken ct)`.
  - `Task<SuzOutcome<IReadOnlyList<SuzBlock>>> ListBlocksAsync(PurchaseIntent intent, CancellationToken ct)`.
  - `Task<SuzOutcome<SuzBlock>> RecoverBlockAsync(PurchaseIntent intent, string blockId, CancellationToken ct)`.
- `ISuzSigner.SignAsync(byte[] payload, string certificateThumbprint, bool detached, CancellationToken ct) -> Task<byte[]>`.
- `IKizLegalReader.ReadAsync(SuzProfile profile, IReadOnlyList<string> rawCodes, CancellationToken ct) -> Task<IReadOnlyList<KizLegalProof>>`.
- `OperationPolicy.SendAsync(OperationKind operation, Func<HttpRequestMessage> createRequest, CancellationToken ct) -> Task<OperationHttpResult>`. OperationHttpResult chứa status, bytes và RetryAt nhưng ToString/report không chứa body/headers.
- SuzHttpClient constructor nhận HttpClient, ISuzSigner, `Func<SuzProfile,ZnakConfig> resolveCredentials`, OperationPolicy, IWorkflowClock; provider kiểm tra profile owner/environment/credential version trước dùng config. TrueApiKizReader dùng cùng auth provider, không copy credentials vào model persistent.
- \`OperationPolicy(IWorkflowClock clock, Func<double>? jitter=null)\`; jitter mặc định Random.Shared.NextDouble, fixture 0.\n- FakeWorkflowClock.UtcNow bắt đầu `2026-10-02T20:00:00Z`; DelayAsync chỉ tăng clock. SuzFixtureHttp nhận sequence `Func<HttpRequestMessage,HttpResponseMessage>`, Calls theo operation, body/headers chỉ ở memory test.

- [ ] **Step 1: Viết test transport đỏ**

```csharp
// create_200_without_id_is_unknown; create_html_is_unknown
Expect(outcome.Kind == SuzOutcomeKind.Unknown && fixture.CreateCalls == 1,
       "Malformed 2xx must not be successful or retried");
// safe_read_429_persists_server_deadline
Expect(outcome.RetryAt == start.AddSeconds(120), "Retry-After seconds lost");
// safe_read_retries_but_issue_codes_does_not
Expect(safeAttempts == 3 && receiveAttempts == 1, "Operation policy used HTTP verb only");
// signature_uses_exact_payload_and_detached_mode
Expect(signedBytes.SequenceEqual(sentBytes) && signer.Detached,
       "Signature and POST bytes differ");
// true_api_requested_identifier_is_exact
Expect(proofs.Single().CodeHash == expectedHash &&
       proofs.Single().RawStatus == "INTRODUCED", "Proof mapped to the wrong code");
// diagnostic_redacts_credentials_and_raw_codes
Expect(!diagnostic.Contains(token) && !diagnostic.Contains(rawCode) &&
       !diagnostic.Contains(omsConnection), "Credential/code leaked");
```

Thêm cases: Retry-After HTTP-date; 401 XML; 200 error object; malformed JSON; block response thuộc order/GTIN khác; unexpected content type; request factory tạo message mới mỗi SafeRead retry. Fake signer không gọi cryptcp.exe. Test stateful GET đã được server nhận nhưng mất response chỉ có một attempt.

- [ ] **Step 2: Chạy RED**

Run: `dotnet run --project tests/MarketplaceHub.Workflows -c Release -- --group transport`.
Expected: zero unrelated failures, các assertions mới chưa đạt.

- [ ] **Step 3: Implement client/policy, giữ signer Windows hiện có**

Tách cryptcp process logic sang CryptoProSuzSigner, giữ signing timeout **60 giây**, cleanup temp và process kill khi cancel/timeout. Đọc stdout/stderr async đồng thời để không chặn process, diagnostic dùng allowlist/error code thay vì echo payload.

Mỗi HTTP request timeout **45 giây**; không giữ long polling trong một method 30 vòng. ReadOrder trả Pending, coordinator lên lịch lượt sau. SafeRead không quota retry tối đa 3 attempts, base delay 1/2 giây cộng jitter 0–250ms; fake jitter bằng 0. Quota trả RetryAt để caller persist và thoát lượt, không sleep 120 giây trong request.

Operation catalog:

| Operation | Request mẫu từ nguồn đã đọc | Policy |
|---|---|---|
| SuzCreate | POST /api/v3/order | Mutation, 1 |
| SuzStatus/ListOrders/ListBlocks | GET order/status, order/list, order/codes/blocks | SafeRead, tối đa 3 |
| SuzReceiveCodes | GET /api/v3/codes | StatefulRead, 1 |
| SuzRecoverBlock | GET /api/v3/order/codes/retry với block ID đã lưu | Reconcile, 1; không nhận mã mới |
| TrueApiCisesInfo | POST /api/v3/true-api/cises/info?pg=lp | SafeRead, tối đa 3, nhóm 100 mã/lượt |
| Auth challenge/signIn | Luồng ký hiện có | Một exchange mỗi lượt; auth lỗi không được gửi POST mua |
| FBS marketplace writes | Adapter/journal hiện có | Giữ limiter/deadline riêng; coordinator không thêm retry chung |

True API contract đọc từ [tài liệu chính thức](https://docs.crpt.ru/gismt/True_API/). Map response bằng requested CIS exact, không theo array position; đọc status/owner/GTIN/package/special state rõ ràng. TrueApiKizReader nối requested CIS qua CisHash; hai full payload khác tail cùng CIS là conflict, không map thành hai unit. Trong app, raw INTRODUCED ánh xạ InCirculation; EMITTED/APPLIED chưa mở shipping gate. Thiếu field/quyền/mã lạ trả proof Unknown, không suy diễn ready. Không tự hỗ trợ response shape không được fixture contract xác định.

SUZ block paths là contract tham khảo từ public WCode; snapshot minh họa ở `docs/contracts/suz-workflow.md` ghi rõ nguồn và chưa chứng minh capability tài khoản thật. 404/unsupported recovery trả Unsupported và NeedsReconciliation, không thử endpoint khác ngẫu nhiên.

Profile mới default AutoPurchaseEnabled=false. Catalog đầu tiên chỉ có mẫu lp/UNIT/template 10/PRODUCTION được seller chọn rõ; environment/host đúng profile, không tự chuyển import/resale thành production. Profile khác hoặc sandbox chưa có host/contract xác minh ở Unsupported. ContractEnabled chỉ bật sau bước xác nhận capability trong Test Center Task 6; mock không bật cấu hình production.

- [ ] **Step 4: Chạy GREEN**

Run group transport rồi persistence. Expected: zero failures; signer fixture kiểm tra exact bytes; không raw response/secrets trong exception, ToString, crash hoặc report mới. Không gọi SUZ hoặc cryptcp thật trong CI.

- [ ] **Step 5: Commit**

Commit: `feat: add structured SUZ transport and operation retry policy`; ledger ghi source/contract uncertainty và RED→GREEN.

### Task 3: Purchase coordinator phục hồi cùng order/block

**Files:**
- Create: `Services/Suz/KizPurchaseCoordinator.cs`.
- Modify: `AppDatabase.SuzPurchases.cs` (claim/scope lock/checkpoint), `SuzModels.cs`.
- Create: `tests/MarketplaceHub.Workflows/PurchaseTests.cs`, `FakeSuzClient.cs`, `PurchaseFixture.cs`; đăng ký group purchase.

**Interfaces:**
- Consumes: Task 1 persistence; Task 2 ISuzClient/clock/policy.
- Produces:
  - `KizPurchaseCoordinator(AppDatabase db, ISuzClient suz, IWorkflowClock clock)`.
  - `Task<PurchaseResult> EnsureAsync(PurchaseIntentRequest request, CancellationToken ct)`.
  - `Task<PurchaseResult> ResumeAsync(string intentId, CancellationToken ct, WorkflowRunMode mode=WorkflowRunMode.UserRequested)`.
  - `Task<PurchaseResult> ReconcileAsync(string intentId, string? selectedRemoteOrderId, bool sellerConfirmed, CancellationToken ct)`.
  - `PurchaseResult(string IntentId, PurchaseStage Stage, int RecoveredCount, string? RemoteOrderId, DateTimeOffset? RetryAt, string? ErrorCode)`; chưa trả code là available.
- FakeSuzClient lưu Create/Read/Receive/Recover call counts, các scripted outcomes và barriers; `PurchaseFixture.Create()` kết hợp WorkflowFixture, fake SUZ/clock và coordinator. `PurchaseFixture.Reopen()` dùng cùng DB/fake remote state.

- [ ] **Step 1: Viết test purchase đỏ**

```csharp
// repeated_clicks_create_once
Expect(left.IntentId == right.IntentId && suz.CreateCalls == 1,
       "Concurrent requests created two purchases");
// poll_error_keeps_remote_id_after_restart
Expect(result.RemoteOrderId == "SUZ-1" && suz.CreateCalls == 1,
       "Poll failure caused a second order");
// crash_after_create_sending_reconciles
Expect(result.Stage == PurchaseStage.NeedsReconciliation && suz.CreateCalls == 0,
       "Startup resent an ambiguous create");
// lost_receive_uses_existing_block
Expect(suz.ReceiveCalls == 1 && suz.RecoverCalls == 1 &&
       result.RecoveredCount == 2 && suz.CreateCalls == 1,
       "Lost codes response created new order or reissued codes");
// quota_restart_waits
Expect(callsBeforeDeadline == 0 && result.RetryAt == deadline,
       "Restart ignored persisted Retry-After");
// partial_codes_are_kept_without_top_up
Expect(result.RecoveredCount == 1 && suz.CreateCalls == 1,
       "Partial result triggered an automatic replacement purchase");
```

Các case bổ sung: quantity 0 tạo zero HTTP calls; quantity âm reject; rejected order không tự tạo intent khác; two job cùng owner/env/GTIN bị unknown scope chặn; two store không dùng chéo pool; duplicate block/codes không tăng recovered count; config version đổi trước send hoặc response muộn sau delete giữ evidence nhưng không cấp mã; order closed/blocks unavailable -> NeedsReconciliation. ListOrders có hai candidate cùng GTIN/quantity không tự bind. Manual selection sai scope/quantity bị từ chối kể cả sellerConfirmed=true.

- [ ] **Step 2: Chạy RED**

Run: `dotnet run --project tests/MarketplaceHub.Workflows -c Release -- --group purchase`.
Expected: các assertions mới thất bại, không gọi remote service thật.

- [ ] **Step 3: Implement coordinator transition theo bằng chứng**

Quantity=0 trả CodesRecovered/recovered 0, không tạo intent/HTTP; âm throw ArgumentOutOfRangeException trước intent. Tính missing nằm ở Task 4; coordinator không thay quantity intent khi pool đổi.

Giữ keyed semaphore owner/env/GTIN và SQLite claim. Ghi CreateSending trước network; confirmed receipt lưu ID ngay; mọi outcome không xác định sau bắt đầu gửi vào CreateUnknown, không reset Draft. Null/empty remote ID ở update không ghi đè ID cũ.

Startup CreateSending thành CreateUnknown; Downloading thành DownloadUnknown. Reconcile chỉ tự bind khi candidate có correlation proof đủ để xác minh duy nhất; cùng GTIN/quantity không đủ. Không đủ proof trả NeedsReconciliation. Manual bind đọc details và scope exact rồi ghi audit operator acknowledgement.

Có RemoteOrderId thì chỉ đọc order cũ. Ready -> lưu Downloading trước ReceiveCodes. Response/block hợp lệ lưu atomically cả block/code encrypted; parse failure không mất dữ liệu block đã lưu. DownloadUnknown ưu tiên ListBlocks+RecoverBlock theo ID trước bất kỳ ReceiveCodes mới; recover mismatch chặn.

Nếu mã ít hơn quantity hoặc server order closed mà không đủ block, giữ recovered codes và NeedsReconciliation. Không đặt bù. Quota save deadline+stage và trả control; Resume không request trước deadline. Cancel sau send giữ trạng thái chưa rõ và IDs; không tự hủy order hoặc release code. Evidence order/block đã nhận được lưu trước generation/config fence; stale worker chỉ được ghi evidence vào intent còn tồn tại, không tái tạo store/intent đã xóa.

- [ ] **Step 4: Chạy GREEN**

Run groups purchase, transport, persistence.
Expected: zero failures; request counters chứng minh mỗi intent tối đa một create; restart dùng cùng file DB và remote fixture, không fresh cache.

- [ ] **Step 5: Commit**

Commit: `feat: recover KIZ purchases without duplicate orders`; ledger ghi từng scenario lost-response/restart.


### Task 4: Eligibility, cấp phát theo unit và coordinator nhãn ba sàn

**Files:**
- Create: `Services/Fbs/KizEligibilityService.cs`, `FbsLabelJobCoordinator.cs`, `FbsLabelAdapters.cs`, `LabelArtifactStore.cs`, `Services/AppServices.FbsLabelJobs.cs`.
- Modify: `AppDatabase.ScopedKiz.cs`, `AppDatabase.FbsLabelJobs.cs`, `AppServices.cs`, `AppServices.WbShipment.cs`, `AppServices.MarketplaceFbs.cs`, `LicenseAccessService.cs`.
- Create: `tests/MarketplaceHub.Workflows/AllocationTests.cs`, `CoordinatorTests.cs`, `FakeFbsLabelAdapter.cs`; đăng ký allocation/coordinator.
- Modify: FbsState/MockApi fixture và csproj để link shared test support; các Assertions hồi quy cũ giữ nguyên ý nghĩa.

**Interfaces:**
- Consumes: Task 1–3 persistence/purchase/legal reader, existing FBS services and LabelResult.
- Produces:
  - `FbsWorkflowContext(string JobId, int Revision, LabelJobSnapshot Snapshot, SuzProfile? Profile)`.
  - `KizEligibilityService(AppDatabase db, IKizLegalReader legal, IWorkflowClock clock)`.
  - `Task<IReadOnlyList<UnitWorkflowResult>> VerifyAsync(FbsWorkflowContext context, IReadOnlyDictionary<FbsUnitKey,string> codes, CancellationToken ct)`.
  - `IReadOnlyDictionary<FbsUnitKey,string> ReserveScopedKiz(FbsWorkflowContext context, IReadOnlyList<FbsUnitDemand> units)`; DB transaction; trả existing binding cho unit trước cấp mã mới.
  - `void SaveLegalProof(KizScope scope, KizLegalProof proof)`; `void ConfirmPhysicalMark(PhysicalMarkEvidence evidence)`; ghi ở đúng scope/version.
  - `LicenseGateResult CanRunFbsWorkflow()` dùng EvaluateCached với cùng verifier/device/clock rules hiện có, không gọi online add-store gate.
- `IFbsLabelAdapter`:
  - `Task<LabelJobSnapshot> ReadSnapshotAsync(LabelTarget target, CancellationToken ct)`.
  - `Task<IReadOnlyDictionary<FbsUnitKey,string>> ReadAssignedCodesAsync(FbsWorkflowContext context, CancellationToken ct)`.
  - `Task<IReadOnlyList<UnitWorkflowResult>> EnsureMarkedAndPackedAsync(FbsWorkflowContext context, IReadOnlyDictionary<FbsUnitKey,string> codes, CancellationToken ct)`.
  - `Task<IReadOnlyList<VerifiedLabel>> DownloadLabelsAsync(FbsWorkflowContext context, IReadOnlyList<FbsUnitKey> eligibleUnits, CancellationToken ct)`.
- `VerifiedLabel(string RemoteOrderId, LabelResult Label, IReadOnlyList<FbsUnitKey> Units, string SourceOperation, string SourceEvidenceHash)`; chỉ adapter có readback đúng remote target mới tạo.
- `LabelArtifactStore.PersistAsync(FbsWorkflowContext context, VerifiedLabel label, CancellationToken ct) -> Task<LabelArtifact>`; `ValidateAsync(LabelArtifact artifact,CancellationToken ct) -> Task<bool>`.
- `FbsLabelJobCoordinator(AppDatabase db, Func<Marketplace,IFbsLabelAdapter> adapters, KizPurchaseCoordinator purchases, KizEligibilityService eligibility, LicenseAccessService license, LabelArtifactStore artifacts, IWorkflowClock clock)`.
  - `Task<LabelJobResult> StartOrResumeAsync(LabelTarget target, IProgress<LabelJobResult>? progress, CancellationToken ct)`.
  - `Task<LabelJobResult> ResumeAsync(string jobId, IProgress<LabelJobResult>? progress, CancellationToken ct, WorkflowRunMode mode=WorkflowRunMode.UserRequested)`.
  - `Task<LabelJobResult> ReprintAsync(string jobId, int revision, CancellationToken ct)`.
  - `Task<LabelJobResult> CreateRevisionAsync(string jobId, bool sellerConfirmed, CancellationToken ct)`.
- `AppServices(AppDatabase db, MarketplaceGateway api, LicenseAccessService license, FbsWorkflowDependencies? workflowDependencies = null)`: giữ các ctor cũ qua optional argument; default chọn clients thật nhưng không tự enable profile.
- `FbsWorkflowDependencies`: SuzClient, LegalReader, Clock, CodeProtector; tests inject fake, production không cung cấp UI bypass.
- `AppServices.LabelJobs` property coordinator; `ExportFbsLabelsAsync(LabelTarget target, IProgress<LabelJobResult>? progress=null, CancellationToken ct=default) -> Task<LabelJobResult>`.

- [ ] **Step 1: Viết test allocation/coordinator đỏ**

```csharp
// legal_and_physical_are_independent
Expect(afterPhysical.Stage == FbsLabelJobStage.AwaitingLegalState,
       "Physical checkbox bypassed missing legal proof");
// stock_exact_scope_and_gtin
Expect(wrongOwnerAssigned == 0 && wrongGtinAssigned == 0 && otherStoreAssigned == 0,
       "Allocation crossed GTIN, owner, environment or store");
// complete_stock_means_no_purchase
Expect(suz.CreateCalls == 0 && result.Stage == FbsLabelJobStage.LabelsReady,
       "Existing eligible stock should not buy more");
// ambiguous_attach_keeps_same_codes
Expect(firstCodeHash == resumedCodeHash && adapter.MutationCalls == 1,
       "Lost attach response replaced codes or resubmitted mutation");
// remote_snapshot_change_is_blocked
Expect(result.Stage == FbsLabelJobStage.SnapshotChanged && adapter.MutationCalls == 0,
       "Changed units/GTIN/mapping were shipped with stale snapshot");
// label_partial_and_reprint_preserve_manifest
Expect(partial.Stage == FbsLabelJobStage.Partial && partial.Artifacts.Count == 1 &&
       reprintPurchaseCalls == 0, "Partial/reprint falsely completed or bought again");
```

Thêm positive/negative controls: INTRODUCED + statusEx EMPTY đúng scope mở legal gate; EMITTED/APPLIED/RETIRED/unknown/special state chưa hỗ trợ hoặc thiếu owner chặn; prefix alias có một binding; tail khác cùng CIS không cấp thành unit thứ hai; items reordered không đổi revision; quantity giảm hoặc mapping version đổi ở SnapshotChanged; xác nhận revision không đủ khi mutation cũ unknown; mã đã trên hàng dùng code scan hiện có.

Fixture adapter giữ full remote posting/order: label chỉ xuất khi **tất cả unit của remote order đó** ready; không in một label posting nhiều unit cho subset thiếu mã. Batch Partial có thể xuất những remote order đủ, manifest không chứa đơn bị chặn.

Test label file mất/hỏng SHA: lấy lại nhãn đúng remote task, không gọi SUZ hoặc allocate code. Expired/tampered license không mutation; valid signed cache dùng cùng chính sách license. Store deletion trong await chặn phase tiếp theo và late DB write.

- [ ] **Step 2: Chạy RED**

Run group allocation, coordinator. Expected: các assertion cụ thể mới fail. Old suites có fixture chưa cung cấp scope/legal proof được cập nhật ở bước triển khai, không bỏ assertions.

- [ ] **Step 3: Implement eligibility/adapter/coordinator và đổi call sites**

Scoped pool lưu legal proof, allocation và physical evidence riêng. Raw INTRODUCED + owner/env/GTIN/package UNIT đúng và statusEx EMPTY/không có mới đủ legal gate cho catalog lp/UNIT đầu tiên; trạng thái khác không đoán tương đương. Đọc proof trước attach và trước LabelsReady; mất API/quyền không dùng proof chưa xác minh làm xanh.

Mã phát hành mới được giữ cho job để in/dán/phục hồi, chưa xuất shipping label. Physical evidence ràng buộc đúng unit + code hash; profile prerequisites cho phép physical trước legal. Import/scan legacy ghi provenance và NeedsVerification; chỉ reader xác nhận legal state, UI không tự nhập INTRODUCED.

Adapter readback xác minh đầy đủ snapshot/membership/codes, giữ journals hiện có. WB target là supply đã nhận; không CreateSupply/DeliverSupply trong export. Non-WB chỉ dùng members batch đã có; không gọi CreateMarketplaceFbsBatch với new-order selection trong export. Box/exemplar/reordered-codes/substate riêng.

Cập nhật EnsureWbSupplyKizAsync và PackMarketplaceFbsAsync bằng optional `FbsWorkflowContext? workflowContext=null`. Với context, thay việc gọi legacy EnsureKizQuantityAsync bằng scoped reservation và purchase coordinator; không thay remote success conditions. Không context: dựng context từ supply/batch membership hiện có; thiếu target exact thì NeedsAction, không mua theo store/SKU đơn lẻ.

Giữ signature EnsureKizQuantityAsync cho compatibility, nhưng ngừng POST tự động không có purchase context. Đủ mã đã xác minh có thể trả về; thiếu mã trả mã lỗi `workflow_context_required` và hướng dẫn đi qua shipment/batch. FBO/manual không được âm thầm chạy lại helper mua cũ; hồi quy FBO vẫn giữ reservation/nhãn chuẩn bị, nếu cần mua thì chỉ dẫn seller chuẩn bị mã qua workflow có scope.

StartOrResume lập/đọc job, check license/generation/config ở service và trước từng mutation. Tính missing theo unit chưa có binding, trừ stock đủ điều kiện; phải phục hồi intent cùng job/GTIN trước tính mua mới. Codes recovered chưa legal/physical không đếm vào stock ready và cũng không bị coi là lý do tạo intent mới.

Sau attach, đối soát và đọc snapshot mới; một remote atomic target thiếu unit hoặc requirement đổi không packing/label. Artifact giữ original response file, hash/provenance và manifest; validate PDF/PNG đúng loại bằng header/renderer hiện có. LabelResult chỉ có barcode/metadata mà không file nhãn chính thức chưa thành OfficialMarketplaceLabel. Bộ nhãn chuẩn bị/nhãn Data Matrix có kind riêng, không gán official shipping label.

Persist file bằng temp + atomic move dưới PrintJobs hiện có, dùng filename từ job ID, không lấy path từ server/user SKU. Reprint kiểm tra hash; file mất chỉ refetch target nhãn cũ. Revision mới chỉ sau sellerConfirmed và đối soát mọi unknown mutation; shared unit bindings được giữ, nhu cầu mới mới được xử lý.

- [ ] **Step 4: Chạy GREEN**

Run nhóm allocation/coordinator và `dotnet run --project tests/MarketplaceHub.FbsState -c Release`, `dotnet run --project tests/MarketplaceHub.MockApi -c Release`, `dotnet run --project tests/MarketplaceHub.Print -c Release`.
Expected: zero failures, request/body assertions riêng WB/Ozon/Yandex giữ đúng contract. Test fixtures mới dùng signed valid license, profile rõ và fake legal reader; không đổi default production gate để test qua.

- [ ] **Step 5: Commit**

Commit: `feat: coordinate verified FBS label jobs with scoped KIZ`.

### Task 5: UI xuất/tiếp tục/in lại, profile và xác nhận tem vật lý

**Files:**
- Create: `UI/FbsLabelJobDialog.cs`, `KizWorkflowProfileDialog.cs`, `MainForm.FbsLabelJobs.cs`.
- Modify: `MainForm.WbShipment.cs`, `MainForm.MarketplaceFbs.cs`, `MainForm.cs` (chỉ hook menu/profile/store deletion), `Services/AppServices.FbsLabelJobs.cs`.
- Modify: `tests/MarketplaceHub.UI/Program.cs`, csproj link test support; `tests/MarketplaceHub.Workflows/CoordinatorTests.cs`.

**Interfaces:**
- Consumes: Task 4 LabelJobResult/coordinator/profile/artifact.
- Produces:
  - `FbsLabelJobDialog(AppServices app, LabelTarget target)`.
  - `void Apply(LabelJobResult result)`; `void Unsubscribe()` khi dispose; observe job không hủy worker khi chuyển trang.
  - `KizWorkflowProfileDialog(AppServices app, StoreProfile store)`: owner/env/profile/auto purchase, status capability read-only; không tự enable unsupported profile.
  - `AppServices.GetFbsLabelJob(string jobId) -> FbsLabelJob?`.
  - `AppServices.PauseFbsLabelJob(string jobId) -> void`.
  - `Task<IReadOnlyList<LabelJobResult>> ResumePendingFbsJobsAsync(CancellationToken ct)` dùng RecoveryOnly.
  - `Task<LabelJobResult> ConfirmJobPhysicalMarksAsync(string jobId, IReadOnlyList<PhysicalMarkEvidence> units, CancellationToken ct)`.
  - `Task<IReadOnlyList<LabelArtifact>> ExportJobDataMatrixAsync(string jobId,CancellationToken ct)`: artifact kind DataMatrix, OfficialMarketplaceLabel=false.
  - `Task ShowFbsLabelJobAsync(LabelTarget target, CancellationToken pageToken)` trong MainForm partial; pageToken chỉ gắn UI observation, worker dùng lifetime/job token.

- [ ] **Step 1: Viết UI test đỏ**

Theo harness WinForms STA/Pump hiện có, tên và assertions:

```csharp
// switching_page_unsubscribes_without_killing_job
Expect(!newPageWasReplaced && completedJobStillExists,
       "Background completion changed navigation or lost job");
// pause_keeps_remote_id_and_reservation
Expect(savedIntent.RemoteOrderId == beforeId && bindings.SequenceEqual(beforeBindings),
       "Pause released purchase or KIZ");
// readiness_is_not_reported_at_post_success
Expect(!successGreen && displayedStage != "100%",
       "Accepted mutation reported completed");
// physical_confirmation_requires_exact_selected_units
Expect(unselectedUnitUnchanged && legalGateStillClosed,
       "Physical acknowledgement altered units or bypassed legal status");
// export_and_reprint_share_job
Expect(firstJobId == repeatedJobId && purchasesAfterReprint == purchasesBeforeReprint,
       "UI started a replacement job");
```

Kiểm tra control names: `fbsJobStage`, `fbsJobUnits`, `fbsJobResume`, `fbsJobPause`, `fbsJobReprint`, `kizPrintDataMatrix`, `kizPhysicalConfirmed`, `kizWorkflowProfile`. Viewport 1044x768 và 1366x768, dark/light: action buttons không khuất, chỉ grid cuộn ngang. Progress hiển thị unit verified/total, Partial không xanh.

- [ ] **Step 2: Chạy RED**

Run: `dotnet run --project tests/MarketplaceHub.UI -c Release`.
Expected: assertions/controls mới fail đúng ca; ảnh RED dùng fixture, không credential thật.

- [ ] **Step 3: Wire UI vào cùng job, không gọi legacy export path để bypass gate**

ExportWbSupplyAsync/ExportMarketplaceBatchCoreAsync chuyển sang ShowFbsLabelJobAsync với exact target. Pack button non-WB có thể mở cùng job/context; mọi đường export/reprint/manual-ready phải qua coordinator. Các action chuyển sang giao hàng giữ boundary hiện có và yêu cầu codes/legal/physical/readback đúng, không dùng một checkbox cũ để bypass.

Dialog hiển thị sàn/store/target, các bước có tác động (chuẩn bị KIZ, packing nếu contract yêu cầu), unit/error, timestamp và hành động phù hợp. Không gọi network từ render/control setters hoặc mở dialog đồng bộ.

Dừng chỉ cancel worker, giữ evidence/reservation; close/unsubscribe không cancel job. App shutdown cancel background IO và persist ambiguity; startup liệt kê pending jobs, chỉ SafeRead/poll/reconcile target cũ được auto resume sau check license/scope.

Profile UI không mặc định seller là nhà sản xuất. Seller chọn profile được hỗ trợ và owner/env rõ; unsupported reason hiển thị trước submit. Physical grid yêu cầu scan/match code với đúng unit, xuất Data Matrix khi cần và xác nhận các unit đã chọn; không sửa legal proof. Nếu còn AwaitingLegalState, hướng dẫn hoàn tất ở Честный ЗНАК rồi bấm kiểm tra lại.

Partial chỉ mở artifacts của những remote order đủ; số tổng và lý do chặn giữ visible. In lại mở artifact cùng revision; không đánh dấu đã in từ Process.Start hoặc tạo file. Store deletion gọi pause/fence trước DeleteStore, tránh deferred event ghi lại dữ liệu.

- [ ] **Step 4: Chạy GREEN và xem ảnh**

Run UI và group coordinator; Expected zero failures. Xem ảnh dark/light ở hai viewport, trang sau navigation, trạng thái Partial/AwaitingLegal/AwaitingPhysical/LabelsReady. Dùng view_image để xác nhận trực quan; không chỉ dựa assertion tọa độ.

- [ ] **Step 5: Commit**

Commit: `feat: expose resumable FBS label and KIZ workflow UI`.


### Task 6: Test Center cho workflow, regression và bản portable

**Files:**
- Create: `Core/WorkflowProbeModels.cs`, `Services/Fbs/WorkflowProbeService.cs`, `tests/MarketplaceHub.Workflows/ProbeTests.cs`.
- Modify: `UI/MainForm.IntegrationTestCenter.cs`, `Services/AppServices.FbsLabelJobs.cs`, `tests/MarketplaceHub.UI/Program.cs`, `tests/MarketplaceHub.License/Program.cs`.
- Modify: hai workflow CI, README và execution ledger.

**Interfaces:**
- Consumes: Task 1–5 job/intent/profile/coordinator và Test Center existing exact-target gates.
- Produces:
  - `WorkflowProbeTarget(string ProfileId, string JobId, int Revision, WorkflowProbeAction Action, string Gtin, int Quantity)`; Action = ReadOnly, BuySuzOne, AttachKizOneOrder, VerifyLabelOneOrder.
  - `WorkflowProbeProof(string Id, WorkflowProbeTarget Target, int StoreGeneration, int ProfileVersion, string CredentialVersion, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt)`.
  - `WorkflowProbeGrant(WorkflowProbeProof Proof, bool SellerConfirmed)`.
  - `WorkflowProbeResult(bool Success, string Code, string RedactedReport, WorkflowProbeProof? Proof, string? IntentId)`.
  - `WorkflowProbeService.ReadOnlyAsync(WorkflowProbeTarget target,CancellationToken ct) -> Task<WorkflowProbeResult>`.
  - `WorkflowProbeService.RunAsync(WorkflowProbeGrant grant,CancellationToken ct) -> Task<WorkflowProbeResult>`.
  - `AppServices.WorkflowProbes` property; service tạo proof registry local theo Id.
- `Task<LabelJobResult> ReauthorizeFbsJobAsync(string jobId, string profileId, WorkflowProbeGrant grant, CancellationToken ct)` cho job cũ sau credential rotation: business scope/payload giữ nguyên, chỉ authorization version cập nhật sau proof exact. Proof expiry **5 phút**, clock Task 1.
- ReadOnly không gọi SuzReceiveCodes, RecoverBlock, CreateOrder hoặc marketplace writes. Proof dùng credential version/session ID, không snapshot/hash raw credential.

- [ ] **Step 1: Viết probe/gate test đỏ**

```csharp
// read_only_has_no_stateful_requests
Expect(createCalls == 0 && receiveCalls == 0 && recoveryCalls == 0 &&
       marketplaceWrites == 0, "Read-only probe issued a stateful request");
// purchase_probe_is_one_confirmed_unit
Expect(!unconfirmed.Success && !twoUnits.Success && !wrongProfile.Success &&
       confirmedSuzCreateCalls == 1, "Probe scope or quantity gate bypassed");
// changed_credential_or_expired_proof_closes_gate
Expect(!staleCredential.Success && !expired.Success && remoteCalls == 0,
       "A stale proof authorized a purchase");
// preview_cannot_expand_to_other_order
Expect(remoteOrderIds.SequenceEqual(new[]{selectedOrderId}),
       "Single-target probe included another pending job/order");
// successful_probe_does_not_enable_other_profile
Expect(!otherProfile.ContractEnabled && !profile.AutoPurchaseEnabled,
       "Probe silently enabled automatic purchases");
// read_only_and_report_redaction_are_complete
Expect(!report.Contains(rawCode) && !report.Contains(token) &&
       !report.Contains(omsConnection), "Probe report leaked sensitive data");
```

UI positive controls cho WB/Ozon/Yandex workflow kind: có exact proof + checkbox mới mở đúng action; legacy WB/National Catalog kinds giữ scope/gate cũ. ReadOnly 401/unsupported/missing permission không mở mutation. Device mismatch/expired license chặn service kể cả gọi trực tiếp bỏ qua UI. Đổi GTIN, quantity, revision, owner/environment sau preview làm mất proof.

- [ ] **Step 2: Chạy RED**

Run: `dotnet run --project tests/MarketplaceHub.Workflows -c Release -- --group probe`, rồi UI/License.
Expected: ca mới fail đúng ranh giới. Không nhập/dùng API key thật ở CI.

- [ ] **Step 3: Implement probes và CI packaging boundary**

ReadOnly xác minh profile/scope, chứng thư/private key còn dùng được, auth, current target/details và quyền đọc cần thiết. Chưa có bằng chứng owner/scope/capability phù hợp thì trả precise unsupported/needs action và không mở mua.

RunAsync kiểm tra proof đăng ký, chưa hết 5 phút, scope/current generation/credential/profile version exact, license hợp lệ và sellerConfirmed. BuySuzOne chỉ quantity=1 và một GTIN, hiển thị hành động mua có chi phí trước click; tạo purchase intent bình thường bằng context probe và chỉ cho phép đúng intent này qua ContractEnabled gate đang chờ thử. Không tạo bypass global hoặc trực tiếp gọi SuzHttpClient bỏ journal.

Confirmed create+poll/code/block proof của đúng profile có thể ghi capability cho profile đó. AutoPurchaseEnabled vẫn do seller chọn trong profile UI. Legal/physical gates vẫn áp dụng; phép thử mua không tự gửi introduction document hoặc báo hàng sẵn sàng.

FBS probe chỉ một external order với đầy đủ units của order; job nhiều order bị từ chối thay vì mở rộng selection. Cùng action restart đi qua journal cũ, không tạo request mới. Profile chưa hỗ trợ nghiệp vụ khác vẫn Unsupported. Sau credential rotation, ReauthorizeFbsJobAsync yêu cầu proof mới, giữ owner/environment/product group/release method/CIS type/GTIN/quantity/payload hash bất biến; chỉ đổi authorization credential version để đọc lại/tiếp tục. Scope/business thay đổi bị từ chối, không sửa intent đã gửi.

Thêm nhánh feature vào build workflow và suite workflows vào regression/build checks. Giữ installer steps disabled; portable publish self-contained win-x64 chỉ sau toàn bộ suite success. Không đổi release/version để giả là installer mới đã hoàn tất.

README có cách chạy profile/physical/legal/recovery và bảng limitation theo capability đã test. Ledger ghi SHA/run IDs, số pass/fail thật, ảnh và probe chưa chạy. Không mang số 196 test của commit cũ sang đợt mới.

- [ ] **Step 4: Chạy GREEN đầy đủ, review và verify portable**

Run trên Windows:
```text
dotnet build MarketplaceHub/MarketplaceHub.csproj -c Release
dotnet run --project tests/MarketplaceHub.Contracts -c Release
dotnet run --project tests/MarketplaceHub.Fbs -c Release
dotnet run --project tests/MarketplaceHub.ProductSync -c Release
dotnet run --project tests/MarketplaceHub.FbsState -c Release
dotnet run --project tests/MarketplaceHub.License -c Release
dotnet run --project tests/MarketplaceHub.MockApi -c Release
dotnet run --project tests/MarketplaceHub.Workflows -c Release
dotnet run --project tests/MarketplaceHub.UI -c Release
dotnet run --project tests/MarketplaceHub.Print -c Release
```

Expected: build exit 0; mọi suite zero failures, Workflows chạy đủ persistence/transport/purchase/allocation/coordinator/probe khi không có --group. Đọc fresh logs của chính SHA chốt.

Chạy whole-branch review theo requesting-code-review; sửa finding xác nhận bằng test tái hiện và chạy lại checks liên quan. Sau đó publish/portable self-test/startup + ảnh UI. Tải artifact của đúng run, so SHA256 với CI. Không tuyên bố physical printing hoặc API thật đã qua từ fixture.

Chỉ sau mock/portable mới bàn giao seller nhập credential trực tiếp, ReadOnly trước, một mục tiêu ghi sau preview/checkbox. Nếu live capability/proof thất bại, giữ gated profile và đối soát; không mở installer. Yêu cầu kiểm tra mạng thực tế từ Nga và máy in thuộc vòng seller thực hiện, không thay bằng suy đoán từ runner.

- [ ] **Step 5: Commit và handoff**

Commit: `test: gate live FBS KIZ probes and verify portable workflows`.
Dùng verification-before-completion và finishing-a-development-branch sau fresh evidence. Giữ nhánh/PR để xem xét, không merge main. Báo rõ code/mock đã đạt ở commit nào, API thật/máy in còn phần nào, installer chưa phát hành.

## Độ phủ đặc tả

| Spec | Task |
|---|---|
| 1–3 Mục tiêu, bối cảnh và kiến trúc | Header/file map, Task 3–5 |
| 4 Platform, secret, data/license và release constraints | Task 1–6 |
| 5 Ranh giới thành phần | File map và Interfaces từng task |
| 6 Identity, revision, intent, unit/scope/concurrency | Task 1/3/4 |
| 7 Export flow và khác biệt ba sàn | Task 4/5 |
| 8 SUZ recovery/quota/cancel | Task 2/3 |
| 9 Profile/legal/physical/legacy | Task 1/2/4/5/6 |
| 10 UI/sub-state/navigation/sync | Task 4/5 |
| 11 Acceptance/test/live gates | Test steps toàn plan, Task 6 |
| 12 Scope và native execution | Header/Task 6; module đợt sau giữ ngoài phạm vi |

## Trạng thái lúc bàn giao plan

Đặc tả đã được duyệt. Kế hoạch này chưa được thực thi; chưa có code sản phẩm/test mới, dependency mới, lần build mới hoặc API thật trong bước lập kế hoạch. Các checkbox chỉ đánh dấu khi có bằng chứng của commit mới.

Phương thức native được giữ. Sau người dùng xem và duyệt plan, tiếp tục từ Task 1 qua executing-plans; không yêu cầu chọn phương thức lại.

## Nguồn contract

- App base commit và spec commit nêu ở header.
- [True API CRPT chính thức](https://docs.crpt.ru/gismt/True_API/): read proof/cises info; đọc ngày 02/10/2026.
- [WCode public source](https://github.com/rupphi/test-wcode/tree/8f7c3d1ac8598158029b8d037e2d3c128e240727): mẫu order/block recovery, không chứng minh contract/capability tài khoản production.
- [Yandex official OpenAPI](https://github.com/yandex-market/yandex-market-partner-api), adapter marketplace giữ contract đã kiểm tra trong repo; schema/community SDK khác chỉ là nguồn tham khảo.
