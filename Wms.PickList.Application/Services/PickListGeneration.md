# Pick List Generation

Orchestrates creation of warehouse pick lists from persisted inventory allocations. All generation paths run through **`PickListGenerationService`** and a shared **5-step pipeline**, followed by **worker assignment (Step 6)** outside the DB transaction.

---

## Architecture overview

```
Entry point (API / background / command)
      │
      ▼
PickListGenerationService
      ├── GenerateAsync              (standard / route / cluster batch)
      ├── GenerateShortPickAsync     (short-pick remaining qty)
      └── GenerateSortItemsPickAsync (sort remaining items on route)
      │
      ▼
DB transaction
      ├── Step 1  LoadExistingAllocations
      ├── Step 1b ValidateFromBins
      ├── Step 2  SetPriority
      ├── Step 3  SelectDropZone
      ├── Step 4  SortAllocationLines
      └── Step 5  BreakIntoPickLists
      │
      ▼
Commit transaction
      │
      ▼
Step 6 AssignWorker (outside transaction, per pick list)
```

**Pipeline location:** `Wms.PickList.Application/Services/Generation/`

**Orchestrator:** `PickListGenerationService.cs`

---

## Prerequisites

Before pick list generation:

1. **`WmsOrder` + `WmsOrderItem`** rows must exist.
2. **`OrderInventoryAllocationService.AllocateAsync`** must have created **`InventoryAllocation`** rows (unlinked — `PickListItemId == null`) for order items. This happens automatically after `SaveOrderCommand` via `OrderInventoryAllocationBackgroundService`.
3. For **route** orders: order should be linked via **`RouteOrder`** and route should have **`RouteBinAssignment`** (from `ProcessOrderDispatchCommand`).

Generation **reads** existing allocations; it does not call `AllocateAsync` except in the short-pick **route** flow (`GenerateShortPickListForRouteCommand` calls `AllocateShortfallAsync` before queuing).

---

## Entry points

All paths eventually call `PickListGenerationService`. The table lists every trigger in the system.

| # | Entry | Trigger | Service method | Sync / async | Notes |
|---|--------|---------|----------------|--------------|-------|
| 1 | **`OrderInventoryAllocationBackgroundService`** | Quick order saved → `orderInventoryAllocationQueue` | `GeneratePickListForOrderCommand` → `GenerateAsync` | Async (in-process queue) | Auto pick list after quick order allocation + CLUSTER stage progress |
| 2 | **`POST /picklist/generate`** (`GeneratePickListsCommand`) | Manager triggers route batch generation | `GenerateAsync` via `GeneratePickListsBackgroundService` | API returns 202; generation async | Selects PENDING routes, marks INITIATED, queues order IDs |
| 3 | **`POST /picklist/generate/{orderId}`** (`GeneratePickListForOrderCommand`) | Manual single-order generation | `GenerateAsync` | Sync | Order must have `IsPicklistCreated != true` |
| 4 | **`POST /orders/generate-cluster-bulk-picking`** (`BulkOrdersCommand`) | Cluster bulk picking for standard orders | `GeneratePickListsForOrdersCommand` → `GenerateAsync` via `ClusterPickListGenerationBackgroundService` | API sync; generation async | `useClusterPriority: true`, creates CLUSTER `OrderStageProgress` |
| 5 | **`GeneratePickListsForOrdersCommand`** | Direct command (used by cluster background service) | `GenerateAsync` with options | Sync when invoked directly | Batch of order IDs |
| 6 | **`POST` short pick for order** (`GenerateShortPickListForOrderCommand`) | Sorting UI — single order short pick | `GenerateShortPickAsync` | Sync | Remaining shortfall qty only |
| 7 | **`POST` short pick for route** (`GenerateShortPickListForRouteCommand`) | Sorting UI — whole route short pick | `GenerateShortPickAsync` via `GenerateShortPickListsBackgroundService` | API returns in-progress; generation async | Calls `AllocateShortfallAsync` first |
| 8 | **`RequestSortItemsCommand`** | Sorting — request remaining items pick | `GenerateSortItemsPickAsync` via `GenerateSortItemsPickListsBackgroundService` | API returns in-progress; generation async | Routable bins with storage type only |

### API surfaces

| API project | Endpoints |
|-------------|-----------|
| **`Wms.Api`** / **`Wms.PickList.Api`** | `POST /picklist/generate`, `POST /picklist/generate/{orderId}` |
| **`Wms.Api`** / **`Wms.Order.Api`** | `POST /orders/generate-cluster-bulk-picking` |
| **`Wms.Api`** / **`Wms.Sorting.Api`** | Short pick (order + route), sort items request |

### Background services & queues

| Queue | Background service | Registered in |
|-------|-------------------|---------------|
| `OrderInventoryAllocationQueue` | `OrderInventoryAllocationBackgroundService` | `AddOrderInventoryAllocation()` |
| `GeneratePickListsQueue` | `GeneratePickListsBackgroundService` | `AddPickListBackgroundGeneration()` |
| `ClusterPickListGenerationQueue` | `ClusterPickListGenerationBackgroundService` | `AddOrderInventoryAllocation()` |
| `GenerateShortPickListsQueue` | `GenerateShortPickListsBackgroundService` | `AddPickListBackgroundGeneration()` |
| `GenerateSortItemsPickListsQueue` | `GenerateSortItemsPickListsBackgroundService` | `AddPickListBackgroundGeneration()` |

---

## `PickListGenerationService`

**Location:** `Wms.PickList.Application/Services/PickListGenerationService.cs`

**Dependencies:**

| Dependency | Role |
|------------|------|
| `WmsDbContextWrite` | Persistence |
| `ICurrentUser` | Worker assignment (Step 6) |
| `OrderInventoryAllocationService` | Passed to Step 1 (reserved for allocation extensions) |

### `GenerateAsync(orders, options?)` — standard generation

Used for: quick auto-generation, route batch, cluster batch, manual single order.

**Flow:**

1. Open DB transaction.
2. Build `PickListGenerationContext` from orders + options.
3. Run pipeline Steps 1 → 5.
4. `SaveChangesAsync` + commit.
5. **`AssignWorkersAsync`** — Step 6 outside transaction (failures per pick list are swallowed).

**Options** (`PickListGenerationOptions`):

| Option | Default | Effect |
|--------|---------|--------|
| `FixedPriority` | `null` | When set, Step 2 uses this instead of order-based rules (cluster batch uses `5`) |
| `UseRouteOnlyDropZone` | `false` | Step 3 treats all orders as route-linked for drop zone selection |
| `WorkerIndex` | `0` | Round-robin start index for worker assignment |

### `GenerateShortPickAsync(order, shortPickItems)` — short-pick regeneration

**Context flags:**

- `ShortPickOrderItems` = items with remaining shortfall
- `FixedPriority = 0`
- `UseRouteOnlyDropZone = true`

**Pre-processing (in service):** Resets short items to `ConfirmedQuantity = 0`, `Status = PENDING`.

**Step 1 differences:** `RequestedQty = TotalItemQuantity - ConfirmedQuantity` (remaining qty only).

**Step 1b skipped:** Allocations from route final-zone bins are kept (short-pick work must continue).

**Step 5:** Does **not** set `order.IsPicklistCreated` (short-pick is additive).

### `GenerateSortItemsPickAsync(orders, sortOrderItems)` — sort remaining items

**Context flags:**

- `SortOrderItems` = specific items across route orders to pick for sorting
- `FixedPriority = 0`
- `UseRouteOnlyDropZone = true`

Processes all route orders in a single batch context so Step 5 consolidates items from multiple orders into unified pick lists per drop location. Uses full order item quantity for requested qty. Same pipeline otherwise.

---

## Pipeline steps (detailed)

Steps are chained via `PickListGenerationStepBase.ChainTo()` — each step calls the next after its own work.

```
Step1LoadExistingAllocations
  → Step1ValidateFromBins
  → Step2SetPriority
  → Step3SelectDropZone
  → Step4SortAllocationLines
  → Step5BreakIntoPickLists

(after commit)
Step6AssignWorker  (via PickListGenerationService.AssignWorkersAsync)
```

---

### Step 1 — `Step1LoadExistingAllocations`

**Purpose:** Build in-memory drafts from persisted `InventoryAllocation` rows.

#### 1a. `BuildOrderItemDrafts`

For each order, determines which order items to process:

| Context | Items processed |
|---------|-----------------|
| Normal | All `WmsOrderItems` on the order |
| `ShortPickOrderItems` set | Only those items (with shortfall or picked qty) |
| `SortOrderItems` set | Only those items |

Creates `OrderItemAllocationDraft` per item with `RequestedQty` and empty `BinAllocations` list.

#### 1b. Load allocations from DB

Queries `InventoryAllocations` where:

- `OrderItemId` in draft list
- `PickListItemId == null` (not yet on a pick list)
- `IsLeftover != true`, active, not deleted
- **Excludes** allocations already in **FINAL_ZONE + IsRoutable** bins (picked/sorted stock)

Includes bin, aisle, zone, batch, location batch navigation properties.

#### 1c. Load bin skills

`BinSkillMappings` → certification skill ID + capacity per bin (used in Step 5 splitting).

#### 1d. Populate drafts

For each order item:

- Maps each allocation → `BinAllocationDraft` (bin, batch, qty, pickable, routable, storage type, skill, sequence)
- Sets item status:
  - **Fully allocated** → `INVENTORY_ASSIGNED`, `ItemsFullyAllocated++`
  - **Partially allocated** → `SHORT_PICK`, `ItemsPartiallyAllocated++`
  - **No allocations** → `INVENTORY_INSUFFICIENT`, `ItemsUnallocated++`

---

### Step 1b — `Step1ValidateFromBins`

**Purpose:** Remove order items whose **only** source bins are route final-zone bins (already at sort drop point).

**Skipped when:** `ShortPickOrderItems` is set (short-pick must include those bins).

**Logic:**

1. Find bins that are `IsRoutable` with `StorageTypeUuid == null` (route final-zone bins).
2. Remove matching bin allocations from drafts.
3. Remove drafts with zero bin allocations → `ItemsSkippedRouteFinalZone++`.

Other items continue through the pipeline.

---

### Step 2 — `Step2SetPriority`

**Purpose:** Assign pick priority per order item.

Uses `PickListPriorityResolver.Resolve(order, FixedPriority)`:

| Condition | Priority |
|-----------|----------|
| `FixedPriority` set in context | Use fixed value |
| Quick + Horecca customer | 1 |
| Quick (other) | 2 |
| Standard + Horecca | 3 |
| Standard (no cluster) | 4 |
| Standard + has `ClusterId` | 5 |
| Default | 4 |

Lower number = higher priority in Step 4 sort.

---

### Step 3 — `Step3SelectDropZone`

**Purpose:** Assign **`DropLocationId`** (where picker drops completed items) per order item draft.

Loads:

- `RouteOrders` → order-to-route map
- `RouteBinAssignments` → route-assigned drop bins
- Zone settings (`EnableSeparateZonePicking` per storage-type zone)
- All warehouse bins (for FINAL_ZONE and storage-type zones)

#### Drop zone rules

**Short-pick items:** Always FINAL_ZONE **IsRoutable** bin.

**Route-linked orders** (`UseRouteOnlyDropZone` OR order in `RouteOrders`):

| From-bin situation | Drop bin |
|--------------------|----------|
| From bin already routable | FINAL_ZONE IsRoutable |
| Separate zone picking ON | IsRoutable bin in storage-type zone |
| Separate zone picking OFF | Bin from `RouteBinAssignment` for route |
| Fallback | FINAL_ZONE IsRoutable |

**Cluster orders** (has `ClusterId`, not route-linked):

| Setting | Drop bin |
|---------|----------|
| Separate zone picking ON | IsClusterBased bin in storage-type zone |
| Separate zone picking OFF | FINAL_ZONE IsClusterBased |

Increments `DropZoneStepsExecuted` when a drop bin is assigned.

---

### Step 4 — `Step4SortAllocationLines`

**Purpose:** Flatten all bin allocations into sortable lines and order the pick walk.

Each bin allocation → `PickListAllocationLineDraft` (carries order, item, bin, skill, drop zone, status).

**Sort order:**

1. Priority (asc)
2. Storage type ID
3. Skill ID
4. Bin sequence
5. Bin ID

Output: `context.AllocationLines` — ordered list defining optimal pick path.

---

### Step 5 — `Step5BreakIntoPickLists`

**Purpose:** Group sorted lines into one or more **`PickList`** entities and link allocations.

#### 5a. `BuildPickListDrafts` — splitting rules

Walks sorted lines and starts a **new pick list** when:

- Drop location, storage type, priority, or skill changes, **OR**
- Skill capacity / max unique SKU limit is reached

Allocations from the same bin stay in the same pick list unless skill capacity is reached (where the overflow splits across pick lists).

#### 5b. Persist pick lists

For each draft:

1. Generate number: `GeneratePicklistNumberWH(warehouseId)`
2. Create `PickList` (PENDING, drop location, priority)
3. Group lines by order item → `PickListItem` (requested/allocated qty, status)
4. Link existing `InventoryAllocation` rows → set `PickListItem`, `Sequence`
5. Add to DB + `context.PickLists`

Increments `PickListsCreated`.

#### 5c. Mark orders generated

For normal generation (not short-pick): sets `order.IsPicklistCreated = true` when any item from that order landed on a pick list → `OrdersMarkedGenerated++`.

---

### Step 6 — `Step6AssignWorker`

**Purpose:** Create **PICK** work tasks and assign workers (round-robin).

**Runs outside the generation transaction** in `PickListGenerationService.AssignWorkersAsync`:

1. `LoadAssignableWorkersAsync` — `GetAssignableWorkersQuery` with `TaskType = PICK`
2. For each pick list in `PENDING` status:
   - `AddTaskCommand` → task name `Pick-{PickListNumber}`, reference `PickListUuid`
   - Assign next worker (`WorkerIndex % workerCount`)
   - Set `pickList.AssignedWorkerId`
   - Save per pick list

Assignment failures are caught and ignored (pick list stays unassigned for later).

Increments `WorkersAssigned`, `WorkerAssignmentStepsExecuted`.

---

## Context & draft objects

**`PickListGenerationContext`** (`PickListGenerationContext.cs`):

| Property | Purpose |
|----------|---------|
| `Orders` | Input orders |
| `OrderItemDrafts` | Step 1 output |
| `AllocationLines` | Step 4 output |
| `PickLists` | Step 5 output |
| `ShortPickOrderItems` / `SortOrderItems` | Restrict which items are processed |
| `FixedPriority` / `UseRouteOnlyDropZone` | Pipeline options |
| `Counters` | Metrics for result |
| `WorkerIndex` | Round-robin state |

**Draft types** (`PickListGenerationDrafts.cs`):

- `OrderItemAllocationDraft` — one order line + its bin allocations + drop zone + priority
- `BinAllocationDraft` — one source bin/batch allocation
- `PickListAllocationLineDraft` — one flattened pick line
- `PickListDraft` — in-memory group of lines before persistence

---

## Result: `GeneratePickListsResult`

| Counter | Meaning |
|---------|---------|
| `OrdersProcessed` | Orders in the batch |
| `PickListsCreated` | New pick list rows |
| `OrdersMarkedGenerated` | Orders flagged `IsPicklistCreated` |
| `OrdersDiscontinued` | Reserved (not currently incremented in pipeline) |
| `DropZoneStepsExecuted` | Items that received a drop location |
| `WorkerAssignmentStepsExecuted` | Pick lists that got worker assignment attempts |
| `ItemsFullyAllocated` | Items with full qty allocated |
| `ItemsPartiallyAllocated` | Items marked SHORT_PICK |
| `ItemsUnallocated` | Items with no bin stock |
| `ItemsSkippedRouteFinalZone` | Items skipped (already at route final zone) |
| `WorkersAssigned` | Successful worker assignments |

---

## End-to-end flows by scenario

### Quick order (automatic)

```
SaveOrderCommand
  → OrderInventoryAllocationQueue
  → OrderInventoryAllocationBackgroundService
       ├── AllocateAsync (bin reservations)
       ├── OrderStageProgress (CLUSTER)
       └── GeneratePickListForOrderCommand
            └── PickListGenerationService.GenerateAsync
```

### Standard route order (manual batch)

```
ProcessOrderDispatchCommand (route + RouteBinAssignment)
  → [picking waits]
  → POST /picklist/generate
       └── GeneratePickListsCommand
            ├── Select PENDING routes → INITIATED
            └── GeneratePickListsQueue
                 └── GeneratePickListsBackgroundService
                      └── GenerateAsync (all route orders)
```

### Standard cluster bulk

```
POST /orders/generate-cluster-bulk-picking (BulkOrdersCommand)
  → OrderStageProgress (CLUSTER)
  → ClusterPickListGenerationQueue
  → ClusterPickListGenerationBackgroundService
       └── GeneratePickListsForOrdersCommand (priority = 5)
            └── GenerateAsync
```

### Short pick (route)

```
POST short-pick for route
  → GenerateShortPickListForRouteCommand
       ├── AllocateShortfallAsync (new bin allocations for shortfall)
       └── GenerateShortPickListsQueue
            └── GenerateShortPickListsBackgroundService
                 └── GenerateShortPickAsync
```

### Sort remaining items

```
RequestSortItemsCommand
  → GenerateSortItemsPickListsQueue
  → GenerateSortItemsPickListsBackgroundService
       └── GenerateSortItemsPickAsync
```

---

## Related files

| File | Responsibility |
|------|----------------|
| `PickListGenerationService.cs` | Orchestrator + 3 generation modes |
| `PickListGenerationContext.cs` | Shared state + priority resolver |
| `PickListGenerationDrafts.cs` | In-memory draft types |
| `PickListGenerationOptions.cs` | Batch options |
| `PickListGenerationStepBase.cs` | Chain-of-responsibility base |
| `Step1LoadExistingAllocations.cs` | Load allocations → drafts |
| `Step1ValidateFromBins.cs` | Filter route final-zone sources |
| `Step2SetPriority.cs` | Priority assignment |
| `Step3SelectDropZone.cs` | Drop location rules |
| `Step4SortAllocationLines.cs` | Pick path sorting |
| `Step5BreakIntoPickLists.cs` | Split + persist pick lists |
| `Step6AssignWorker.cs` | PICK task + worker assignment |
| `OrderInventoryAllocationService.cs` | Upstream bin allocation |
| `GeneratePickListsCommand.cs` | Route batch entry |
| `GeneratePickListForOrderCommand.cs` | Single order entry |
| `GeneratePickListsForOrdersCommand.cs` | Multi-order entry |
| `GenerateShortPickListForOrderCommand.cs` | Short pick (order) |
| `GenerateShortPickListForRouteCommand.cs` | Short pick (route) |
| `RequestSortItemsCommand.cs` | Sort items entry |
| `PickListApplicationDependencyInjection.cs` | Background queue registration |
