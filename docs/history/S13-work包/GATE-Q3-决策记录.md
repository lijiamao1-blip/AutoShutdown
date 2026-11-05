# GATE-Q3 决策记录（旧 runtime.json 处理策略）

> 裁决日期：2026-08-15
> 决策来源：S13-T02B 正式通过后、进入 T03 前的 Gate 裁决（人工拍板）
> 阻断对象：S13-T03 迁移验收项（原「逐任务迁移」）

---

## 1. 结论（已定）

**旧 V1 `runtime.json`（单实例 `CurrentInstance`，`SchemaVersion=1`）采用「备份后重建」，不执行逐任务迁移。**

- 触发：V2 首次启动检测到旧 V1 `runtime.json`。
- 处理：先备份 `runtime.json → runtime.json.v1bak`（满足架构书 §4.3 回滚），再以**空的多实例运行态**开始。
- 版本判定：以「`SchemaVersion=1` 且为单实例 `CurrentInstance` 结构」为准（沿用架构书 §4.2 的 config.json 版本检测作为升级入口，但 runtime.json 自身的迁移判定按此规则）。

---

## 2. 裁决依据

1. **runtime.json 是瞬态运行时状态**（在途实例 + `StageToken` + `HasExecuted`），不是用户持久配置；升级/重启后其中的在途倒计时本就失效，丢掉不损失用户价值。
2. **真实 V1 结构与架构书 §4.2 描述不符**：§4.2 称 v1 使用 `"default"` 键、按任务 id 组织；但真实 V1 代码（`State/RuntimeState.cs` + `Scheduling/SchedulerEngine.cs`）是 `CurrentInstance` 单实例，**没有 `"default"` 键、没有按 id 字典**。§4.2「逐任务映射」的前提在真实数据中不存在。
3. **V2 任务来自 tasks.json（V2 新增，无 V1 对应物）**：V1 那个单一实例在 V2 里**没有可映射的 task id**。
4. 因此「逐任务迁移」在数据上不可行——没有源键、没有目标 id，硬做只能伪造 id，违背「不猜」原则。

---

## 3. 对 T03 的约束

- T03 的 runtime.json 读写/迁移行为 = **备份 V1 单实例文件 + 以空多实例态重建**，**不做逐任务映射**。
- 迁移验收项由「逐任务映射」改为「备份 V1 单实例文件 + 空多实例态重建」，并保留测试覆盖：
  - V1 `SchemaVersion=1` 单实例文件被备份为 `.v1bak`；
  - 备份后以空多实例态启动（不残留 V1 单实例、不伪造 task id）；
  - 无旧文件时直接以空态开始（NotFound 语义不变）；
  - 损坏数据沿用 Storage 的 Corrupt 语义（不回退为空态静默吞掉）。
- §4.3 备份要求**仍强制**，不回退。

---

## 4. 遗留登记

- **架构书 §4.2「v1 用 `"default"` 键」与真实 V1 `CurrentInstance` 不符**：已在本决策中裁决为「备份后重建」从而绕过该描述；**不据此改写冻结架构书**（避免越界修改冻结文档），仅在 T03 结果记录中标注该差异。
- 该差异不再触发 T03 停止条件「发现 V1 runtime.json 实际结构与架构书不符」——因为它已在 GATE-Q3 阶段被裁决覆盖。
