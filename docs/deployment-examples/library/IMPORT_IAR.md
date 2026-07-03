# Import IAR to Library Workflow

Use script:

`tools/import_iar_to_library.sh`

## Dry run

```bash
./tools/import_iar_to_library.sh \
  --iar /home/marty/opensim/abody_igrid.iar \
  --first Shared \
  --last Inventory
```

## Execute

```bash
./tools/import_iar_to_library.sh \
  --iar /home/marty/opensim/abody_igrid.iar \
  --first Shared \
  --last Inventory \
  --target-path / \
  --compose-dir /home/marty/opensim \
  --service grid-main \
  --tmux-session opensim_session \
  --execute
```

## Notes

- Default command template uses:
  `load iar --merge "{FIRST}" "{LAST}" "{TARGET_PATH}" "{IAR_PATH}"`
- If your OpenSim build uses another syntax, pass `--cmd-template`.
- On execute, script optionally bumps `Libraries.xml` `RootVersion` (+1).
