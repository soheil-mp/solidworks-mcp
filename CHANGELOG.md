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
- Tools (136):
  - session: `sw_status`, `sw_connect`, `sw_help`, `dialog_list`, `dialog_respond`;
  - documents: `doc_new` (part, assembly, drawing), `doc_open` (native files and STEP/IGES/Parasolid/SAT/STL imports),
    `doc_list`, `doc_activate`, `doc_save`, `doc_close`, `doc_rebuild`, `doc_undo`, `doc_units_set`;
  - inspection: `model_info`, `feature_list`, `face_list`, `edge_list`, `vertex_list`, `body_list`, `model_measure`,
    `model_mass`, `model_check`, `model_cut_list` (weldment and sheet-metal cut lists), `view_screenshot`;
  - sketch: `sketch_start`, `sketch_add`, `sketch_finish`, `sketch_edit`, `sketch_info`, `sketch_dimension_add`,
    `sketch_relation_add`, `sketch_trim`, `sketch_extend`, `sketch_offset`, `sketch_fillet`, `sketch_chamfer`,
    `sketch_pattern`, `sketch_mirror`, `sketch_convert`, `sketch_construction_set`, `sketch_delete`;
  - features: `feature_extrude`, `feature_revolve`, `feature_sweep`, `feature_loft`, `feature_fillet`,
    `feature_chamfer`, `feature_shell`, `feature_hole`, `feature_draft`, `feature_rib`, `feature_helix`,
    `feature_thread_cosmetic`, `feature_pattern_linear`, `feature_pattern_circular`, `feature_mirror`,
    `feature_info`, `feature_delete`, `feature_suppress_set`, `feature_rename`, `feature_rollback`,
    `dimension_list`, `dimension_set`, `dimension_tolerance_set` (bilateral, limit, symmetric, basic, min/max, ISO fits);
  - reference geometry: `ref_plane`, `ref_axis`, `ref_point`, `ref_coordsys`;
  - bodies and surfaces: `body_move`, `body_delete`, `body_combine`, `body_split`, `face_edit` (delete and heal, offset,
    move faces of any body, imported ones too), `surface_extrude`,
    `surface_revolve`, `surface_planar`, `surface_offset`, `surface_knit`, `surface_thicken`;
  - sheet metal: `sheetmetal_base_flange`, `sheetmetal_edge_flange`, `sheetmetal_miter_flange` (around corners and
    closed trays), `sheetmetal_hem`, `sheetmetal_bend` (sketched bend), `sheetmetal_flatten`, `sheetmetal_export_flat`;
  - weldments: `weldment_member` (structural members along 2D or 3D sketch lines), `weldment_finish` (trim/extend,
    end caps, gussets, weld beads);
  - assembly: `asm_insert`, `asm_mate_add` (also limit distance/angle, symmetric, width, hinge, gear and rack-and-pinion
    mates), `asm_mate_list`, `asm_component_list`, `asm_component_set`,
    `asm_component_move`, `asm_interference`, `asm_replace`, `asm_pattern` (linear, circular, mirror, opposite-hand parts),
    `asm_explode` (exploded views), `asm_bom` (bill of materials as data or CSV: top level, parts only, indented);
  - drawing: `drw_sheet_setup`, `drw_sheet_add`, `drw_views_standard`, `drw_view_projected`, `drw_view_section`, `drw_view_detail`,
    `drw_view_add` (flat-pattern and auxiliary views), `drw_view_edit` (broken views, broken-out sections),
    `drw_dimensions_auto`, `drw_dimension_add` (also hole callouts), `drw_note_add`, `drw_annotation_add` (datum, surface finish, tolerance frame, weld symbol,
    balloons), `drw_table_add` (hole, weldment cut-list and revision tables), `drw_bom_add`, `drw_center_marks`, `drw_info`;
  - export: `model_export`, `model_export_all` (release package: a STEP per model, a PDF per drawing, a flat DXF per
    sheet-metal part, the BOM);
  - properties: `material_list`, `material_set`, `prop_list`, `prop_set`, `prop_delete`, `equation_list`,
    `equation_set`, `equation_delete`, `config_list`, `config_add`, `config_activate`, `config_delete`,
    `appearance_set`;
  - simulation: `sim_static` (fixed, roller, symmetry, hinge fixtures; pressure, force, gravity loads), `sim_run`
    (natural frequencies, buckling load factors), `sim_list`, `sim_delete`;
  - automation: `vba_run` (off by default).
- Tests: unit tests, offline contract tests, live tests per area with exact oracles, and `tools/mcp_smoke.py` with the
  golden scenarios (plate, enclosure, edit, assembly, drawing, sheet metal).
- Install: `tools/install.ps1` publishes into a versioned folder and registers the server with Claude Code; when Smart
  App Control blocks the exe, it registers `dotnet solidworks-mcp.dll` instead.
- Live-tested against SOLIDWORKS 2025 SP1.2: 374 live tests across all areas pass. Fixes found that way include:
  - coupled system options (STL quality with deviation and angle) are saved and restored as one group, also after a
    crash (an STL export had left a user's STL quality at coarse);
  - Hole Wizard holes are placed exactly (a model-space ray pick) and through-all holes work;
  - the standard material databases are found, so materials and simulation work;
  - body_split goes through Intersect; surface_knit can close a surface into a solid; sheet-metal reverse works;
  - drawing views, sections, details, BOMs, model-edge dimensions and sheet scales work (an empty sheet that SOLIDWORKS
    rescales on its first view gets the planned scale back);
  - measurements with arcs report the minimum distance; assembly mates report their real entity kinds.

