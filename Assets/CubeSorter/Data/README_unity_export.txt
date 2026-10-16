BALL SORTER (colored balls, gravity tracks, GBC-style ball pumps) - UNITY NOTES
===========================================================================
This replaces the earlier cube/conveyor concept. Geometry is procedural: Scripts/Layout.cs is the single source of truth (mm, Blender frame:
X along the gantry, Y away from the visitor, Z up). Unity position = (-X, Z, -Y) * 0.001.
Data/layout.json is exported from Layout.cs and read by scripts/cs_build_balls.py (Blender concept model) so the model matches the sim.

Files
- plc_tags.csv : mock PLC tag table (X_ = inputs to PLC, Y_ = outputs, DS_/DF_ = int/real). Names match Tg in TagTable.cs.
- layout.json  : derived dimensions for Blender.
Code: Core.cs (paths, ball ledger types) / Layout.cs / MachineSim.cs (simulated machine, implements IMachineIO) / PlcProgram.cs (mock PLC + MachineRunner) / MachineView.cs (3D view + IMGUI) / OrbitCamera.cs.
Later hardware: implement IMachineIO with Modbus against the BRX tags above; PlcProgram.cs is the logic to port to ladder.
