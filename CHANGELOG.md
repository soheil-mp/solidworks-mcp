# Changelog

## 0.1.0 (unreleased)
- Foundation:
  - STA COM dispatcher with timeouts, BUSY, late completions, early DIALOG_BLOCKING and a cross-process lock;
  - Win32 dialog watchdog with provenance;
  - target-document ownership (owned/adopted/foreign);
  - path policy with backups;
  - per-process preference journal with compare-and-swap restore;
  - verified feature creation with automatic rollback;
  - entity references by id, point, query and name.
- Tools:
  - session: `sw_status`, `sw_connect`, `dialog_list`, `dialog_respond`;
  - documents: `doc_new`, `doc_list`, `doc_activate`, `doc_save`, `doc_close`;
  - inspection: `model_info`, `feature_list`, `face_list`, `edge_list`, `vertex_list`, `view_screenshot`;
  - sketch and features: `sketch_start`, `sketch_add`, `sketch_finish`, `feature_extrude`, `feature_fillet`;
  - automation: `vba_run` (off by default).
