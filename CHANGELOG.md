# Changelog

## 0.1.0 (unreleased)
- Foundation:
  - STA COM dispatcher with timeouts, BUSY, late completions, early DIALOG_BLOCKING and a cross-process lock;
  - Win32 dialog watchdog with provenance;
  - target-document ownership (owned/adopted/foreign);
  - path policy with backups;
  - per-process preference journal with compare-and-swap restore;
  - verified feature creation with automatic rollback;
  - entity references by id, point, query and name;
  - compact input schemas (the EntityRef fields are explained once, in the server instructions).
- Tools (121):
  - session: `sw_status`, `sw_connect`, `sw_help`, `dialog_list`, `dialog_respond`;
  - documents: `doc_new` (part, assembly, drawing), `doc_open` (native files and STEP/IGES/Parasolid/SAT/STL imports),
    `doc_list`, `doc_activate`, `doc_save`, `doc_close`, `doc_rebuild`, `doc_undo`, `doc_units_set`;
  - inspection: `model_info`, `feature_list`, `face_list`, `edge_list`, `vertex_list`, `body_list`, `model_measure`,
    `model_mass`, `model_check`, `view_screenshot`;
  - sketch: `sketch_start`, `sketch_add`, `sketch_finish`, `sketch_edit`, `sketch_info`, `sketch_dimension_add`,
    `sketch_relation_add`, `sketch_trim`, `sketch_extend`, `sketch_offset`, `sketch_fillet`, `sketch_chamfer`,
    `sketch_pattern`, `sketch_mirror`, `sketch_convert`, `sketch_construction_set`, `sketch_delete`;
  - features: `feature_extrude`, `feature_revolve`, `feature_sweep`, `feature_loft`, `feature_fillet`,
    `feature_chamfer`, `feature_shell`, `feature_hole`, `feature_draft`, `feature_rib`, `feature_helix`,
    `feature_thread_cosmetic`, `feature_pattern_linear`, `feature_pattern_circular`, `feature_mirror`,
    `feature_info`, `feature_delete`, `feature_suppress_set`, `feature_rename`, `feature_rollback`,
    `dimension_list`, `dimension_set`;
  - reference geometry: `ref_plane`, `ref_axis`, `ref_point`, `ref_coordsys`;
  - bodies and surfaces: `body_move`, `body_delete`, `body_combine`, `body_split`, `face_edit` (delete and heal, offset,
    move faces of any body, imported ones too), `surface_extrude`,
    `surface_revolve`, `surface_planar`, `surface_offset`, `surface_knit`, `surface_thicken`;
  - sheet metal: `sheetmetal_base_flange`, `sheetmetal_edge_flange`, `sheetmetal_flatten`, `sheetmetal_export_flat`;
  - assembly: `asm_insert`, `asm_mate_add`, `asm_mate_list`, `asm_component_list`, `asm_component_set`,
    `asm_component_move`, `asm_interference`, `asm_replace`, `asm_pattern` (linear, circular, mirror);
  - drawing: `drw_sheet_setup`, `drw_views_standard`, `drw_view_projected`, `drw_view_section`, `drw_view_detail`,
    `drw_dimensions_auto`, `drw_dimension_add`, `drw_note_add`, `drw_annotation_add` (datum, surface finish, tolerance frame,
    balloon), `drw_bom_add`, `drw_center_marks`, `drw_info`;
  - export: `model_export`;
  - properties: `material_list`, `material_set`, `prop_list`, `prop_set`, `prop_delete`, `equation_list`,
    `equation_set`, `equation_delete`, `config_list`, `config_add`, `config_activate`, `config_delete`,
    `appearance_set`;
  - simulation: `sim_static`, `sim_list`, `sim_delete`;
  - automation: `vba_run` (off by default).
- Tests: unit tests, offline contract tests, live tests per area with exact oracles, and `tools/mcp_smoke.py` with the
  golden scenarios (plate, enclosure, edit, assembly, drawing).
- Live-tested against SOLIDWORKS 2025 SP1.2: 328 live tests across all areas pass. Fixes found that way include:
  - coupled system options (STL quality with deviation and angle) are saved and restored as one group, also after a
    crash (an STL export had left a user's STL quality at coarse);
  - Hole Wizard holes are placed exactly (a model-space ray pick) and through-all holes work;
  - the standard material databases are found, so materials and simulation work;
  - body_split goes through Intersect; surface_knit can close a surface into a solid; sheet-metal reverse works;
  - drawing views, sections, details, BOMs, model-edge dimensions and sheet scales work;
  - measurements with arcs report the minimum distance; assembly mates report their real entity kinds.

