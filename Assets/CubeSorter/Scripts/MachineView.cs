using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CubeSorter
{
    /// <summary>
    /// Revision 2 view: builds the ball sorter from Layout (procedural geometry, Blender frame mm -> Unity metres), runs the mock PLC + simulated machine,
    /// animates pistons, diverter flaps, gates and the rotary chute from sim state, and draws the visitor pendant, HMI banner, operator strip and developer overlay with IMGUI.
    /// </summary>
    public class MachineView : MonoBehaviour
    {
        // ---- configurable from the Inspector / dev overlay ----
        public float ballDiameter = 20f;
        public int inventoryPerColor = 50;
        public bool presentation = true;          // true = visitor view (no labels / diagnostics); P toggles developer mode
        public float speed = 1f;
        public Preset startPreset = Preset.PreloadedDemo;

        public Layout L; public MachineSim Sim; public MachineRunner R;
        Transform root;
        Material baseOpaque, baseClear;
        readonly Dictionary<int, Material> matCache = new Dictionary<int, Material>();
        Material[] ballMat = new Material[7];
        readonly List<Transform> ballT = new List<Transform>();
        public static readonly Color[] BallCol = {
            Color.gray, new Color(0.86f, 0.08f, 0.08f), new Color(0.98f, 0.46f, 0.03f), new Color(0.08f, 0.25f, 0.95f),
            new Color(0.05f, 0.62f, 0.2f), new Color(0.96f, 0.96f, 0.94f), new Color(0.04f, 0.04f, 0.05f) };

        // animated parts
        Transform[] pistonRod = new Transform[6], pistonHead = new Transform[6], pawlA = new Transform[6], pawlB = new Transform[6], flap = new Transform[6];
        Transform stopper, seatGate, pocketGate, rotor; Transform[] visGate = new Transform[2];
        Vector3 stopperBase, seatGateBase; Vector3[] visGateBase = new Vector3[2]; Vector3 pocketGateBase;
        Vector3[] flapAutoDir = new Vector3[6], flapVisDir = new Vector3[6];
        Material lensMat;
        bool estop;

        // HMI state
        int hmiSeen; readonly Queue<HmiMsg> hmiQ = new Queue<HmiMsg>(); HmiMsg hmiCur; float hmiSince; string demoLine = "";
        GUIStyle sLabel, sBig, sSmall, sBtn, sBox; readonly Texture2D[] swatch = new Texture2D[7]; Texture2D darkTex;
        Vector2 mouseDownPos; Camera cam; OrbitCamera orbit;
        Vector2 devScroll;

        // ================= setup =================
        void Awake()
        {
            presentation = true;
            baseOpaque = Resources.Load<Material>("BS_Opaque"); baseClear = Resources.Load<Material>("BS_Clear");
            for (int c = 1; c <= 6; c++) { ballMat[c] = new Material(baseOpaque); ballMat[c].SetColor("_BaseColor", BallCol[c]); ballMat[c].SetFloat("_Smoothness", c == 6 ? 0.8f : 0.6f); swatch[c] = Tex(BallCol[c]); }
            darkTex = Tex(new Color(0.08f, 0.1f, 0.14f, 0.82f));
            cam = Camera.main; if (cam) orbit = cam.GetComponent<OrbitCamera>();
            Rebuild(startPreset, true);
        }

        static Texture2D Tex(Color c) { var t = new Texture2D(2, 2); var px = new Color[4]; for (int i = 0; i < 4; i++) px[i] = c; t.SetPixels(px); t.Apply(); return t; }

        public void Rebuild(Preset preset, bool start)
        {
            if (root) Destroy(root.gameObject);
            matCache.Clear(); ballT.Clear();
            var root0 = new GameObject("Machine"); root0.transform.SetParent(transform, false); root = root0.transform;
            L = new Layout { BallD = ballDiameter, InventoryPerColor = inventoryPerColor }; L.Build();
            Sim = new MachineSim(L, 1, preset); R = new MachineRunner(Sim, 1);
            estop = false; hmiSeen = 0; hmiQ.Clear(); hmiCur = null; demoLine = "";
            Build();
            if (start) Press("X_Start_PB");
            SetPresets();
        }

        public void Restart() { Rebuild(Sim != null ? Sim.Current : startPreset, true); }

        void Press(string tag) { R.Tags.SetBool(tag, true); R.Advance(R.ScanTime); R.Tags.SetBool(tag, false); R.Advance(R.ScanTime); }

        // ================= geometry helpers =================
        static Vector3 U(double x, double y, double z) { return new Vector3(-(float)x, (float)z, -(float)y) * 0.001f; }
        static Vector3 U(V3 p) { return U(p.X, p.Y, p.Z); }

        Material Mat(Color c, float smooth = 0.35f)
        {
            int key = ((int)(c.r * 255) << 16) | ((int)(c.g * 255) << 8) | (int)(c.b * 255);
            Material m;
            if (!matCache.TryGetValue(key, out m)) { m = new Material(baseOpaque); m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth); matCache[key] = m; }
            return m;
        }
        static readonly Color Steel = new Color(0.62f, 0.65f, 0.7f), Dark = new Color(0.16f, 0.17f, 0.2f), Deck = new Color(0.82f, 0.84f, 0.88f), Accent = new Color(0.1f, 0.4f, 0.75f),
            AutoCol = new Color(0.42f, 0.62f, 0.85f), VisCol = new Color(0.96f, 0.66f, 0.1f), Orange = new Color(0.95f, 0.5f, 0.1f), Red = new Color(0.9f, 0.2f, 0.1f);

        Transform Prim(PrimitiveType t, string name, Transform parent, Material m, bool collider = false)
        {
            var g = GameObject.CreatePrimitive(t); g.name = name; g.transform.SetParent(parent ? parent : root, false);
            if (!collider) { var c = g.GetComponent<Collider>(); if (c) Destroy(c); }
            g.GetComponent<Renderer>().sharedMaterial = m; return g.transform;
        }
        Transform Box(string n, double cx, double cy, double cz, double sx, double sy, double sz, Material m, Transform parent = null)
        {
            var t = Prim(PrimitiveType.Cube, n, parent, m); t.localPosition = U(cx, cy, cz); t.localScale = new Vector3((float)sx, (float)sz, (float)sy) * 0.001f; return t;
        }
        Transform BoxBox(string n, double x0, double x1, double y0, double y1, double z0, double z1, Material m, Transform parent = null)
        {
            return Box(n, (x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2, Math.Abs(x1 - x0), Math.Abs(y1 - y0), Math.Abs(z1 - z0), m, parent);
        }
        Transform Cyl(string n, double cx, double cy, double z0, double z1, double r, Material m, Transform parent = null)
        {
            var t = Prim(PrimitiveType.Cylinder, n, parent, m); t.localPosition = U(cx, cy, (z0 + z1) / 2);
            t.localScale = new Vector3((float)(2 * r), (float)(Math.Abs(z1 - z0) / 2), (float)(2 * r)) * 0.001f; return t;
        }
        // cylinder along an arbitrary segment
        Transform SegCyl(string n, V3 a, V3 b, double r, Material m, Transform parent = null)
        {
            var t = Prim(PrimitiveType.Cylinder, n, parent, m);
            Vector3 ua = U(a), ub = U(b), d = ub - ua; if (d.sqrMagnitude < 1e-12f) d = Vector3.up;
            t.localPosition = (ua + ub) * 0.5f; t.localRotation = Quaternion.FromToRotation(Vector3.up, d.normalized);
            t.localScale = new Vector3((float)(2 * r) * 0.001f, d.magnitude * 0.5f, (float)(2 * r) * 0.001f); return t;
        }
        // plate along a segment: width w (across), thickness h (local up), centre offset 'down' mm below the segment line
        Transform Seg(string n, V3 a, V3 b, double w, double h, double down, Material m, double side = 0, Transform parent = null)
        {
            var t = Prim(PrimitiveType.Cube, n, parent, m);
            Vector3 ua = U(a), ub = U(b), d = ub - ua; if (d.sqrMagnitude < 1e-12f) d = Vector3.forward;
            var rot = Quaternion.LookRotation(d.normalized, Vector3.up);
            t.localRotation = rot; t.localPosition = (ua + ub) * 0.5f + rot * new Vector3((float)side, (float)-down, 0) * 0.001f;
            t.localScale = new Vector3((float)w, (float)h, d.magnitude * 1000f) * 0.001f; return t;
        }
        void Trough(string n, Path p, int from, int to, Material floor, Material rail, Transform parent = null)
        {
            double w = L.BallD * 0.96;
            for (int i = from; i < to; i++)
            {
                V3 a = p.P[i], b = p.P[i + 1];
                if (V3.Dist(a, b) < 0.5) continue;
                Seg(n + "_floor", a, b, w + 6, 3, L.Rb + 1.5, floor, 0, parent);
                Seg(n + "_railL", a, b, 2.5, L.Rb * 0.9, 0, rail, w / 2 + 1.2, parent);
                Seg(n + "_railR", a, b, 2.5, L.Rb * 0.9, 0, rail, -(w / 2 + 1.2), parent);
            }
        }
        void Post(string n, double x, double y, double ztop, double r, Material m) { Cyl(n, x, y, 0, ztop, r, m); }

        /// <summary>Surface of revolution about the vertical axis; profile = (radius, dz) pairs in mm relative to zBase. Double sided.</summary>
        Transform Lathe(string n, double cx, double cy, double zBase, double[] prof, Material m, Transform parent = null, int seg = 36)
        {
            int np = prof.Length / 2; var verts = new List<Vector3>(); var tris = new List<int>();
            for (int i = 0; i < np; i++) for (int j = 0; j <= seg; j++)
                { double a = 2 * Math.PI * j / seg; verts.Add(new Vector3((float)(prof[2 * i] * Math.Cos(a)), (float)prof[2 * i + 1], (float)(prof[2 * i] * Math.Sin(a))) * 0.001f); }
            int nv = verts.Count; for (int i = 0; i < nv; i++) verts.Add(verts[i]);     // separate vertex set for the back faces so normals do not cancel
            for (int i = 0; i < np - 1; i++) for (int j = 0; j < seg; j++)
            {
                int a0 = i * (seg + 1) + j, a1 = a0 + 1, b0 = a0 + seg + 1, b1 = b0 + 1;
                tris.Add(a0); tris.Add(b0); tris.Add(a1); tris.Add(a1); tris.Add(b0); tris.Add(b1);
                tris.Add(nv + a0); tris.Add(nv + a1); tris.Add(nv + b0); tris.Add(nv + a1); tris.Add(nv + b1); tris.Add(nv + b0);
            }
            var mesh = new Mesh(); mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var g = new GameObject(n); g.transform.SetParent(parent ? parent : root, false); g.transform.localPosition = U(cx, cy, zBase);
            g.AddComponent<MeshFilter>().sharedMesh = mesh; g.AddComponent<MeshRenderer>().sharedMaterial = m; return g.transform;
        }

        // ================= build the machine =================
        void Build()
        {
            var steel = Mat(Steel, 0.6f); var dark = Mat(Dark, 0.4f); var deck = Mat(Deck, 0.25f); var acc = Mat(Accent, 0.4f);
            BoxBox("Base", L.MinX, L.MaxX, L.MinY - 130, L.MaxY, -16, 0, deck);
            BoxBox("BaseEdge", L.MinX, L.MaxX, L.MinY - 130, L.MinY - 126, -16, 6, acc);
            for (int k = 0; k < 6; k++) { BuildHopper(k); BuildPump(k); }
            BuildBulk(); BuildTower(); BuildReturn(); BuildReject(); BuildPendant(); BuildBalls();
        }

        void BuildHopper(int k)
        {
            var steel = Mat(Steel, 0.6f); var clear = baseClear;
            double s = L.Side[k], hx = L.HopperX[k], hy = L.HopperY[k], D = L.HopperDepth, W = L.HopperW, Rb = L.Rb;
            Color hc = BallCol[k + 1]; var hm = Mat(hc, 0.3f); var printed = Mat(Color.Lerp(hc, Color.white, 0.72f), 0.25f);
            double xIn = hx - s * D / 2, xOut = hx + s * D / 2, zTop = L.HopperRimZ + 6;
            double vIn = -D / 2 + 3, vOut = D / 2 - 3;
            V3 fa = new V3(xIn + s * 3, hy, L.ZfRear + (L.YRear - vIn) * L.Tilt), fb = new V3(xOut - s * 3, hy, L.ZfRear + (L.YRear - vOut) * L.Tilt);
            Seg("HopperFloor" + k, fa, fb, W, 4, Rb + 2, printed);                                   // printed sloping bottom (rolls toward the pump)
            double zfFront = L.ZfRear + (L.YRear - (-D / 2)) * L.Tilt;
            BoxBox("HopperLip" + k, xIn, xIn + s * 3, hy - W / 2, hy + W / 2, 0, zfFront + L.Cell * 0.7, hm);   // colored identification lip
            BoxBox("HopperInnerWall" + k, xIn, xIn + s * 2, hy - W / 2, hy + W / 2, zfFront + L.Cell * 0.7, zTop, clear);
            BoxBox("HopperSideA" + k, xIn, xOut, hy - W / 2 - 2, hy - W / 2, 0, zTop, clear);        // transparent acrylic walls
            BoxBox("HopperSideB" + k, xIn, xOut, hy + W / 2, hy + W / 2 + 2, 0, zTop, clear);
            double port = L.Cell * 0.6;
            BoxBox("HopperBackA" + k, xOut, xOut + s * 3, hy - W / 2, hy - port, 0, zTop, steel);
            BoxBox("HopperBackB" + k, xOut, xOut + s * 3, hy + port, hy + W / 2, 0, zTop, steel);
            BoxBox("HopperBackTop" + k, xOut, xOut + s * 3, hy - port, hy + port, L.ZfRear + L.Cell * 0.9, zTop, steel);
            BoxBox("HopperBackLow" + k, xOut, xOut + s * 3, hy - port, hy + port, 0, L.ZfRear - Rb - 2, steel);
            Trough("Ramp" + k, L.Ramp[k], 1, 2, steel, steel);
            // return-tube inlet funnel in the hopper color
            var inl = L.Inlet[k];
            Lathe("InletFunnel" + k, inl.X, inl.Y, inl.Z, new double[] { 17, 12, 11, 9, L.TubeOuterR + 0.5, -Rb - 3 }, hm);
            // supports (legs) for the hopper
            BoxBox("HopperLegA" + k, xIn + s * 8, xOut - s * 8, hy - W / 2 + 4, hy - W / 2 + 8, 0, L.ZfRear - Rb - 2, steel);
            BoxBox("HopperLegB" + k, xIn + s * 8, xOut - s * 8, hy + W / 2 - 8, hy + W / 2 - 4, 0, L.ZfRear - Rb - 2, steel);
        }

        void BuildPump(int k)
        {
            var steel = Mat(Steel, 0.6f); var dark = Mat(Dark, 0.4f); var acc = Mat(Accent, 0.4f); var clear = baseClear; var org = Mat(Orange);
            double s = L.Side[k], px = L.PumpX[k], py = L.HopperY[k], Rb = L.Rb;
            BoxBox("PumpHousing" + k, px - 24, px + 24, py - 24, py + 24, 0, L.ZCup - Rb - 30, dark);
            pistonRod[k] = Cyl("PistonRod" + k, px, py, 0, 1, 4.5, steel);
            pistonHead[k] = Cyl("PistonHead" + k, px, py, 0, 1, L.TubeInnerR - 0.6, acc);
            double zi0 = L.ZCup - Rb - 4, zi1 = L.ZCup + Rb + 1;
            BoxBox("InletBack" + k, px + s * (Rb + 1), px + s * (Rb + 5), py - Rb - 5, py + Rb + 5, zi0, zi1, dark);
            BoxBox("InletA" + k, px - s * (Rb + 5), px + s * (Rb + 5), py + Rb + 1, py + Rb + 5, zi0, zi1, dark);
            BoxBox("InletB" + k, px - s * (Rb + 5), px + s * (Rb + 5), py - Rb - 5, py - Rb - 1, zi0, zi1, dark);
            pawlA[k] = Box("PawlA" + k, 0, 0, 0, 8, 10, 4, org); pawlB[k] = Box("PawlB" + k, 0, 0, 0, 8, 10, 4, org);
            Cyl("PumpTube" + k, px, py, zi1, L.OutletZ - Rb - 1, L.TubeOuterR, clear);
            // outlet fitting with the selectable diverter flap
            double zo0 = L.OutletZ - Rb - 1, zo1 = L.OutletZ + Rb + 4; V3 dv = L.Diverter(k);
            BoxBox("OutBack" + k, px + s * (Rb + 1), px + s * (Rb + 5), py - Rb - 5, py + Rb + 5, zo0, zo1, dark);
            BoxBox("OutA" + k, px + s * (Rb + 5), dv.X, py + Rb + 1, py + Rb + 5, zo0, zo1, dark);
            BoxBox("OutB" + k, px + s * (Rb + 5), dv.X, py - Rb - 5, py - Rb - 1, zo0, zo1, dark);
            BoxBox("OutCap" + k, px + s * (Rb + 5), dv.X, py - Rb - 5, py + Rb + 5, zo1, zo1 + 4, dark);
            Cyl("DivPin" + k, dv.X, dv.Y, dv.Z - Rb - 3, dv.Z + Rb + 3, 2.2, steel);
            var fl = Prim(PrimitiveType.Cube, "DivFlap" + k, null, Mat(Red)); fl.localScale = new Vector3(3, (float)(2 * Rb + 4), 28) * 0.001f; flap[k] = fl;
            Vector3 a0 = U(L.AutoPath[k].P[1]) - U(L.AutoPath[k].P[0]), v0 = U(L.VisPath[k].P[1]) - U(L.VisPath[k].P[0]);
            a0.y = 0; v0.y = 0; flapAutoDir[k] = a0.normalized; flapVisDir[k] = v0.normalized;
            Post("PumpPost" + k, px - s * 8, py + Rb + 12, L.OutletZ - Rb - 8, 4, steel);
            // collectors: automatic (blue-grey) toward the bulk hopper, visitor (amber) toward the visitor funnel
            Trough("AutoPath" + k, L.AutoPath[k], 0, L.AutoPath[k].P.Length - 1, Mat(AutoCol, 0.4f), steel);
            Trough("VisPath" + k, L.VisPath[k], 0, L.VisPath[k].P.Length - 1, Mat(VisCol, 0.4f), steel);
        }

        void BuildBulk()
        {
            var steel = Mat(Steel, 0.6f); var clear = baseClear; var org = Mat(Orange); var printed = Mat(new Color(0.85f, 0.87f, 0.9f), 0.25f);
            double cy = L.BulkY, R0 = L.BulkR, Rb = L.Rb, cone = Math.Tan(12 * Math.PI / 180);
            double zc = L.BulkExitZ + 12 - Rb - 1;                         // floor surface at the centre
            Lathe("BulkFloor", 0, cy, zc, new double[] { 12, -4, R0, R0 * cone }, printed);            // sloped floor to the centre exit
            Lathe("BulkWall", 0, cy, zc + R0 * cone, new double[] { R0 + 2, 0, R0 + 2, L.BulkWallZ - (zc + R0 * cone) }, clear);   // transparent wall
            Lathe("BulkRim", 0, cy, L.BulkWallZ, new double[] { R0 + 4, -3, R0 + 4, 3 }, org);
            Lathe("BulkExitFunnel", 0, cy, zc - 4, new double[] { 12, 0, L.TubeOuterR, -12 }, steel);
            for (int i = 0; i < 3; i++) { double a = Math.PI / 2 + i * 2 * Math.PI / 3; Cyl("BulkLeg" + i, 0.75 * R0 * Math.Cos(a), cy + 0.75 * R0 * Math.Sin(a), 0, zc + 0.75 * R0 * cone - 3, 6, steel); }
            // accessible batch-loading opening at the back: angled chute with a hinged lid
            V3 ha = new V3(0, cy + R0 + 95, L.BulkWallZ + 55), hb = new V3(0, cy + R0 + 2, L.BulkWallZ + 6);
            Seg("BatchChute", ha, hb, 130, 3, 0, org);
            Seg("BatchChuteL", ha, hb, 3, 26, -10, org, 65); Seg("BatchChuteR", ha, hb, 3, 26, -10, org, -65);
            Seg("BatchLid", new V3(0, cy + R0 + 100, L.BulkWallZ + 56), new V3(0, cy + R0 + 100, L.BulkWallZ + 150), 140, 3, 0, Mat(new Color(0.7f, 0.85f, 1f)));
            // exit trough (the automatic feed), supported from below
            Trough("AutoFeed", L.AutoFeed, 0, L.AutoFeed.P.Length - 1, baseClear, steel);
            Post("FeedPostA", 0, L.StopperY + 15, L.AutoFeed.At(L.StopperS - 15).Z - Rb - 6, 4, steel);
            Post("FeedPostB", 0, 130, L.AutoFeed.At(L.AutoFeed.Length - 190).Z - Rb - 6, 4, steel);
        }

        void BuildTower()
        {
            var steel = Mat(Steel, 0.6f); var dark = Mat(Dark, 0.4f); var acc = Mat(Accent, 0.4f); var clear = baseClear; var red = Mat(Red); var org = Mat(Orange);
            double Rb = L.Rb;
            // visitor funnels + rails (separate from the automatic path until the junction)
            Trough("VisJoinL", L.VisJoinL, 0, L.VisJoinL.P.Length - 1, Mat(VisCol, 0.4f), steel);
            Trough("VisJoinR", L.VisJoinR, 0, L.VisJoinR.P.Length - 1, Mat(VisCol, 0.4f), steel);
            for (int b = 0; b < 2; b++)
            {
                double sx = b == 0 ? -1 : 1; V3 p0 = (b == 0 ? L.VisJoinL : L.VisJoinR).P[0];
                Lathe("VisFunnel" + b, p0.X, p0.Y, p0.Z, new double[] { 46, 14, 40, 10, 14, -6, L.TubeOuterR, -Rb - 4 }, Mat(VisCol, 0.4f));
                Post("VisFunnelPost" + b, p0.X, p0.Y, p0.Z - Rb - 10, 5, steel);
                V3 g = (b == 0 ? L.VisJoinL : L.VisJoinR).At(L.VisWaitS);
                visGateBase[b] = U(g.X - sx * (Rb + 3), g.Y, g.Z + Rb * 0.2);
                visGate[b] = Box("VisGate" + b, 0, 0, 0, 4, 2 * Rb + 4, 2 * Rb + 8, red); visGate[b].localPosition = visGateBase[b];
            }
            // automatic stopper
            V3 sp = L.AutoFeed.At(L.StopperS);
            stopperBase = U(sp.X, sp.Y - Rb - 3, sp.Z + Rb * 0.2);
            stopper = Box("AutoStopper", 0, 0, 0, 2 * Rb + 4, 4, 2 * Rb + 8, red); stopper.localPosition = stopperBase;
            // junction collar
            Cyl("JunctionCollar", 0, L.JunctionY, L.ZJunction - Rb - 4, L.ZJunction + Rb + 4, Rb + 5, clear);
            // ---- stage 1: colour seat above a recessed sensor ----
            double zs = L.ZSeat, ys = L.SeatY;
            for (int i = 0; i < 3; i++) { double a = Math.PI / 2 + i * 2 * Math.PI / 3 + 0.5; Cyl("SeatPost" + i, 7 * Math.Cos(a), ys + 7 * Math.Sin(a), zs - Rb - 12, zs - Rb * 0.52, 2.2, steel); }
            Cyl("SeatRing", 0, ys, zs - Rb - 14, zs - Rb - 10, 11, dark);
            Cyl("SensorWell", 0, ys, zs - Rb - 30, zs - Rb - 14, 9, Mat(new Color(0.03f, 0.03f, 0.04f)));        // recess: the ball never touches the board
            BoxBox("SensorPCB", -16, 16, ys - 16, ys + 16, zs - Rb - 36, zs - Rb - 32, Mat(new Color(0.05f, 0.35f, 0.2f)));
            lensMat = new Material(baseOpaque); lensMat.SetColor("_BaseColor", Color.gray);
            var lens = Prim(PrimitiveType.Sphere, "SensorLens", null, lensMat); lens.localPosition = U(0, ys, zs - Rb - 26); lens.localScale = Vector3.one * 0.011f;
            BoxBox("McuBoard", 18, 44, ys - 12, ys + 12, zs - Rb - 36, zs - Rb - 33, Mat(new Color(0.1f, 0.2f, 0.55f)));
            Post("SeatPost", 0, ys + 26, zs - Rb - 36, 6, steel);
            // seat gate + transfer to the holding pocket
            seatGateBase = U(0, ys - Rb - 3, zs + Rb * 0.2);
            seatGate = Box("SeatGate", 0, 0, 0, 2 * Rb + 4, 4, 2 * Rb + 8, red); seatGate.localPosition = seatGateBase;
            Trough("SeatToPocket", L.SeatToPocket, 0, 1, clear, steel);
            // ---- stage 2: holding pocket above the rotary chute ----
            double zp = L.ZPocket;
            Cyl("PocketCollar", 0, 0, zp - Rb - 1, zp + Rb + 8, Rb + 3.5, clear);
            pocketGateBase = U(0, 0, zp - Rb - 3);
            pocketGate = Box("PocketGate", 0, 0, 0, 2 * Rb + 10, 2 * Rb + 10, 3, red); pocketGate.localPosition = pocketGateBase;
            BoxBox("PocketArm", -4, 4, 0, 52, zp - Rb - 12, zp - Rb - 7, steel);
            Post("PocketPost", 0, 52, zp - Rb - 7, 7, steel);
            Cyl("DropTube", 0, 0, L.ChuteZ + 16 - Rb - 2, zp - Rb - 4, L.TubeOuterR, clear);
            // ---- rotary chute (stepper driven) ----
            double zc = L.ChuteZ;
            Post("StepperColumn", 0, 0, 120, 16, steel);
            BoxBox("StepperMotor", -21, 21, -21, 21, 120, 168, dark);
            Cyl("StepperShaft", 0, 0, 168, zc + 8 - Rb - 8, 3, steel);
            Cyl("StepperLabelPlate", 0, 0, 140, 146, 22.6, acc);
            var rt = new GameObject("ChuteRotor"); rt.transform.SetParent(root, false); rt.transform.localPosition = U(0, 0, 0); rotor = rt.transform;
            Cyl("RotorHub", 0, 0, zc + 16 - Rb - 8, zc + 16 - Rb - 3, 14, acc, rotor);
            V3 a0 = L.ChuteArm[0].P[1];
            V3 armEnd = new V3(L.StationR, 0, zc);
            Trough("ChuteArm", new Path(new V3(0, 0, zc + 16), armEnd), 0, 1, Mat(Accent, 0.4f), steel, rotor);
            Seg("ChuteArmScoop", new V3(-14, 0, zc + 16 + 3), new V3(8, 0, zc + 16 + 1), 2 * Rb + 6, 3, Rb + 1.5, Mat(Accent, 0.4f), 0, rotor);
            for (int i = 0; i < 7; i++)
            {
                var st = L.Station[i]; var fm = Mat(i < 6 ? BallCol[i + 1] : Red, 0.3f);
                Lathe("StationFunnel" + i, st.X, st.Y, st.Z, new double[] { 17, 6, 16, 3, L.TubeOuterR + 0.5, -Rb - 3 }, fm);
            }
            // sorting-station support deck ring under the funnels
            Cyl("StationDeck", 0, 0, zc - Rb - 34, zc - Rb - 30, 76, Mat(new Color(0.78f, 0.8f, 0.85f), 0.3f));
            Cyl("StationDeckPost", 0, 0, 120, zc - Rb - 34, 5, steel);
        }

        void BuildReturn()
        {
            var steel = Mat(Steel, 0.6f); var dark = Mat(Dark, 0.4f); var tube = baseClear;
            double Rb = L.Rb;
            for (int i = 0; i < 7; i++)
            {
                var p = L.Tube[i]; int n = p.P.Length;
                for (int j = 0; j < n - 1; j++) SegCyl("Tube" + i + "_" + j, p.P[j], p.P[j + 1], L.TubeOuterR, tube);
                var band = Mat(i < 6 ? BallCol[i + 1] : Red, 0.4f);
                for (int j = n - 4; j < n - 1; j++) SegCyl("TubeBand" + i + "_" + j, p.P[j], p.P[j + 1], L.TubeOuterR + 0.6, band);
                int[] cl = { 9, 19 };
                foreach (int j in cl)
                {
                    var q = p.P[j]; Box("Clip" + i + "_" + j, q.X, q.Y, q.Z, 9, 9, 9, dark);
                    Cyl("ClipPost" + i + "_" + j, q.X, q.Y, 0, q.Z - L.TubeOuterR - 3, 2.2, steel);
                }
            }
        }

        void BuildReject()
        {
            var clear = baseClear; var red = Mat(Red); double by = L.RejectBinY, w = 3 * L.Cell + 8, d = Math.Max(1, L.RejectCap / 3) * L.Cell + 8;
            double zf = L.RejectBinZ - 40 - L.Rb - 1;
            BoxBox("RejectFloor", -w / 2, w / 2, by - d / 2, by + d / 2, 0, zf, Mat(new Color(0.55f, 0.1f, 0.1f)));
            BoxBox("RejectW1", -w / 2 - 2, -w / 2, by - d / 2, by + d / 2, 0, L.RejectBinZ + 25, clear);
            BoxBox("RejectW2", w / 2, w / 2 + 2, by - d / 2, by + d / 2, 0, L.RejectBinZ + 25, clear);
            BoxBox("RejectW3", -w / 2, w / 2, by + d / 2, by + d / 2 + 2, 0, L.RejectBinZ + 25, clear);
            BoxBox("RejectW4", -w / 2, w / 2, by - d / 2 - 3, by - d / 2, 0, zf + L.Cell * 0.7, red);
        }

        void BuildPendant()
        {
            var steel = Mat(Steel, 0.6f); var dark = Mat(Dark, 0.4f);
            double py0 = L.MinY - 52;
            BoxBox("PendantBody", -190, 190, py0 - 40, py0 + 40, 0, 26, dark);
            for (int k = 0; k < 6; k++)
            {
                double bx = (k - 2.5) * 56;
                var b = Cyl("PendantButton" + k, bx, py0, 26, 38, 20, Mat(BallCol[k + 1], 0.5f));
                var col = b.gameObject.AddComponent<CapsuleCollider>(); col.height = 2; col.radius = 0.5f; col.direction = 1;
                var rr = Prim(PrimitiveType.Cylinder, "PendantRing" + k, null, steel); rr.localPosition = U(bx, py0, 25); rr.localScale = new Vector3(0.05f, 0.0012f, 0.05f);
            }
        }

        void BuildBalls()
        {
            var sphereMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            for (int i = 0; i < Sim.Balls.Count; i++)
            {
                var g = new GameObject("Ball" + i); g.transform.SetParent(root, false);
                g.AddComponent<MeshFilter>().sharedMesh = sphereMesh; g.AddComponent<MeshRenderer>().sharedMaterial = ballMat[Sim.Balls[i].TrueColor];
                g.transform.localScale = Vector3.one * (float)L.BallD * 0.001f; g.transform.localPosition = U(Sim.Balls[i].Pos);
                ballT.Add(g.transform);
            }
        }

        // ================= camera presets =================
        static readonly string[] CamNames = { "Full machine", "Pump + diverter", "Feed paths + inspection", "Rotary selector + tubes" };
        Vector3[] pf = new Vector3[4]; float[] pd = new float[4], py_ = new float[4], pp = new float[4];
        void SetPreset(int i, double bx, double by, double bz, float d, float yaw, float pitch) { pf[i] = U(bx, by, bz); pd[i] = d; py_[i] = yaw; pp[i] = pitch; }
        void SetPresets()
        {
            SetPreset(0, 0, 70, 215, 1.35f, 0, 27);
            SetPreset(1, L.PumpX[0] * 0.88, L.HopperY[0] + 28, L.OutletZ - 20, 0.36f, -25, 42);
            SetPreset(2, 0, 150, 300, 0.75f, 28, 24);
            SetPreset(3, 0, -10, 255, 0.8f, 0, 38);
            if (orbit) Go(0);
        }
        public int CurrentCam;
        public void GoSnap(int i) { Go(i); if (orbit) orbit.Snap(); }
        public void Go(int i) { CurrentCam = i; if (orbit) { orbit.GoTo(pf[i], pd[i], py_[i], pp[i]); } }

        // ================= per-frame =================
        void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f) * speed;
            R.Advance(dt);
        }

        void LateUpdate()
        {
            if (Sim == null) return;
            double Rb = L.Rb;
            for (int i = 0; i < Sim.Balls.Count && i < ballT.Count; i++) { ballT[i].localPosition = U(Sim.Balls[i].Pos); Sim.Balls[i].Teleport = false; }
            for (int k = 0; k < 6; k++)
            {
                var p = Sim.Pumps[k]; double px = L.PumpX[k], py = L.HopperY[k];
                double headTop = L.ZCup - Rb + p.Piston * L.BallD, headC = headTop - 2;
                double rodTop = headC - 2, rodBot = L.ZCup - Rb - 30;
                SetCyl(pistonHead[k], px, py, headC - 2, headC + 2, L.TubeInnerR - 0.6);
                SetCyl(pistonRod[k], px, py, rodBot, rodTop, 4.5);
                double pawlY = Lerp(Rb + 12, Rb * 0.5 + 2, p.Pawl), pz = L.ZCup + Rb + 3 - 1;
                pawlA[k].localPosition = U(px, py + pawlY, pz); pawlB[k].localPosition = U(px, py - pawlY, pz);
                Vector3 dir = Vector3.Slerp(flapAutoDir[k], flapVisDir[k], (float)p.Div);
                if (dir.sqrMagnitude < 1e-6f) dir = flapAutoDir[k];
                flap[k].localRotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                flap[k].localPosition = U(L.Diverter(k)) + dir.normalized * 0.014f;
            }
            double drop = (L.BallD + 8) * 0.001;
            stopper.localPosition = stopperBase + Vector3.down * (float)(Sim.StopperOpen * drop);
            seatGate.localPosition = seatGateBase + Vector3.down * (float)(Sim.SeatGateOpen * drop);
            for (int b = 0; b < 2; b++) visGate[b].localPosition = visGateBase[b] + Vector3.down * (float)(Sim.VisGateOpen * drop);
            pocketGate.localPosition = pocketGateBase + new Vector3(-1f, 0, 0) * (float)(Sim.PocketGateOpen * (L.BallD + 14) * 0.001);
            rotor.localRotation = Quaternion.Euler(0, (float)-Sim.ChuteAngle, 0);
            Color lc = new Color(0.25f, 0.25f, 0.28f);
            if (Sim.ColorLightOn && Sim.Seat != null) lc = new Color(1f, 0.85f, 0.3f);
            if (Sim.Seat != null && Sim.SeatValid) lc = Sim.SeatColor >= 1 && Sim.SeatColor <= 6 ? BallCol[Sim.SeatColor] : new Color(0.6f, 0.3f, 0.7f);
            lensMat.SetColor("_BaseColor", lc);
        }
        static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        void SetCyl(Transform t, double x, double y, double z0, double z1, double r)
        {
            t.localPosition = U(x, y, (z0 + z1) / 2); t.localScale = new Vector3((float)(2 * r), (float)(Math.Abs(z1 - z0) / 2), (float)(2 * r)) * 0.001f;
        }

        // ================= GUI =================
        void InitStyles()
        {
            if (sLabel != null) return;
            sLabel = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false, richText = true }; sLabel.normal.textColor = Color.white;
            sBig = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true }; sBig.normal.textColor = Color.white;
            sSmall = new GUIStyle(GUI.skin.label) { fontSize = 12 }; sSmall.normal.textColor = new Color(0.85f, 0.9f, 1f);
            sBtn = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            sBox = new GUIStyle(GUI.skin.box); sBox.normal.background = darkTex;
        }

        float gs;      // GUI scale
        float VW { get { return Screen.width / gs; } }
        float VH { get { return Screen.height / gs; } }

        void OnGUI()
        {
            if (Sim == null) return;
            InitStyles();
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.P) { presentation = !presentation; e.Use(); }
            gs = Mathf.Clamp(Screen.height / 760f, 0.85f, 1.8f);
            OrbitCamera.Blockers.Clear();
            var prev = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(gs, gs, 1));
            if (!presentation) DrawLabels();
            DrawHmi();
            DrawPendant();
            DrawOperatorStrip();
            DrawCameraButtons();
            if (!presentation) DrawDev();
            GUI.matrix = prev;
            HandleWorldClick(e);
        }

        void Block(Rect r) { OrbitCamera.Blockers.Add(new Rect(r.x * gs, r.y * gs, r.width * gs, r.height * gs)); }

        void HandleWorldClick(Event e)
        {
            if (!cam) return;
            if (e.type == EventType.MouseDown && e.button == 0) mouseDownPos = e.mousePosition;
            if (e.type == EventType.MouseUp && e.button == 0 && (e.mousePosition - mouseDownPos).magnitude < 6f)
            {
                for (int i = 0; i < OrbitCamera.Blockers.Count; i++) if (OrbitCamera.Blockers[i].Contains(e.mousePosition)) return;
                var ray = cam.ScreenPointToRay(new Vector3(e.mousePosition.x, Screen.height - e.mousePosition.y, 0));
                RaycastHit hit;
                if (Physics.Raycast(ray, out hit, 20f) && hit.collider.name.StartsWith("PendantButton"))
                { int k; if (int.TryParse(hit.collider.name.Substring(13), out k)) Press(Tg.Pend[k]); }
            }
        }

        void Label3D(V3 p, string text, Color c, int size = 12)
        {
            if (!cam) return;
            var sp = cam.WorldToScreenPoint(U(p)); if (sp.z < 0) return;
            float x = sp.x / gs, y = (Screen.height - sp.y) / gs;
            var st = new GUIStyle(sLabel) { fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            var r = new Rect(x - 80, y - 11, 160, 22);
            st.normal.textColor = Color.black; GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, st);
            st.normal.textColor = c; GUI.Label(r, text, st);
        }

        // developer mode only: component labels (one per component, placed apart)
        void DrawLabels()
        {
            if (CurrentCam == 0 || true)
            {
                for (int k = 0; k < 6; k++)
                {
                    double s = L.Side[k]; var p = Sim.Pumps[k]; int stored = Sim.Hopper[k].Count + p.Column.Count + (p.Cup != null ? 1 : 0);
                    Label3D(new V3(L.HopperX[k], L.HopperY[k], L.HopperRimZ + 26), Colors.Names[k + 1].ToUpperInvariant() + " HOPPER " + Sim.Hopper[k].Count, new Color(0.95f, 1f, 0.9f), 11);
                    Label3D(new V3(L.PumpX[k] + s * 40, L.HopperY[k], L.OutletZ + 26), "PUMP " + (k + 1) + " + DIVERTER (" + (p.Div > 0.5 ? "VISITOR" : "AUTO") + ")", new Color(1f, 0.9f, 0.5f), 11);
                }
                Label3D(new V3(0, L.BulkY, L.BulkWallZ + 190), "SHARED BULK HOPPER " + Sim.Bulk.Count, new Color(1f, 0.8f, 0.5f), 12);
                Label3D(new V3(0, L.StopperY + 20, L.ZJunction + 40), "AUTO STOPPER", new Color(1f, 0.7f, 0.6f), 11);
                Label3D(new V3(-150, L.JunctionY, L.ZJunction + 55), "VISITOR PATH", new Color(1f, 0.85f, 0.4f), 11);
                Label3D(new V3(0, L.SeatY + 40, L.ZSeat + 40), "COLOR SEAT + SENSOR", new Color(1f, 0.95f, 0.6f), 11);
                Label3D(new V3(0, -25, L.ZPocket + 36), "HOLDING POCKET", new Color(0.8f, 1f, 0.8f), 11);
                Label3D(new V3(0, -45, L.ChuteZ - 60), "ROTARY CHUTE (STEPPER)", new Color(0.7f, 0.85f, 1f), 11);
                Label3D(new V3(0, L.RejectBinY - 60, L.RejectBinZ + 40), "REJECT BIN " + Sim.RejectBin.Count, new Color(1f, 0.55f, 0.5f), 11);
            }
        }

        // ---- HMI banner (visitor facing) ----
        void DrawHmi()
        {
            var plc = R.Plc;
            if (plc.Hmi.Count < hmiSeen) hmiSeen = Math.Max(0, hmiSeen - 150);
            for (; hmiSeen < plc.Hmi.Count; hmiSeen++)
            {
                var m = plc.Hmi[hmiSeen];
                if (m.Src == ReqSrc.Visitor || m.ReqId == 0) hmiQ.Enqueue(m);
                else demoLine = "Demo ball: " + m.Text;
            }
            float minShow = hmiQ.Count > 3 ? 0.5f : 1.3f;
            if (Event.current.type == EventType.Repaint)
            {
                if (hmiQ.Count > 0 && (hmiCur == null || Time.unscaledTime - hmiSince >= minShow)) { hmiCur = hmiQ.Dequeue(); hmiSince = Time.unscaledTime; }
                else if (hmiQ.Count == 0 && hmiCur != null && Time.unscaledTime - hmiSince > 6f) hmiCur = null;
            }
            string text; Color chip = Color.clear;
            if (hmiCur != null) { text = hmiCur.Text; if (hmiCur.Color >= 1) chip = BallCol[hmiCur.Color]; }
            else if (!plc.Running) text = R.Tags.GetBool("X_EStop_NC") ? (plc.Ph == Phase.Idle ? "Press Start" : "Machine stopped") : "Emergency stop (simulated)";
            else if (plc.Ph == Phase.Sorting) text = "Sorting the starting batch...";
            else if (plc.Ph == Phase.SortDone) text = "Sorting complete - ready to recirculate";
            else if (plc.Ph == Phase.Priming) text = "Priming the pumps...";
            else text = "Choose a color to send a ball around!";
            float w = Mathf.Min(720, VW - 460), x = (VW - w) / 2;
            float top = 10f; var box = new Rect(x, top, w, 70); GUI.Box(box, GUIContent.none, sBox); Block(box);
            if (chip.a > 0) { var o = GUI.color; GUI.color = chip; GUI.DrawTexture(new Rect(x + 12, top + 15, 40, 40), swatch[hmiCur.Color]); GUI.color = o; }
            GUI.Label(new Rect(x + 60, top + 2, w - 120, 66), text, sBig);
            // visitor entries: requested color vs MEASURED color
            var lines = new List<Req>();
            foreach (var r in plc.Live) if (r.Src == ReqSrc.Visitor && r.Stage != RS.Done) lines.Add(r);
            float yy = top + 76;
            if (lines.Count > 0)
            {
                var panel = new Rect(x, yy, w, 6 + lines.Count * 22); GUI.Box(panel, GUIContent.none, sBox); Block(panel);
                for (int i = 0; i < lines.Count; i++)
                {
                    var r = lines[i]; float ly = yy + 5 + i * 22;
                    GUI.Label(new Rect(x + 8, ly - 2, 90, 20), "Requested", sSmall); GUI.DrawTexture(new Rect(x + 74, ly + 1, 14, 14), swatch[r.Color]);
                    GUI.Label(new Rect(x + 96, ly - 2, 80, 20), "Measured", sSmall);
                    if (r.Detected >= 1) GUI.DrawTexture(new Rect(x + 154, ly + 1, 14, 14), swatch[r.Detected]); else GUI.Label(new Rect(x + 154, ly - 2, 20, 20), r.Detected == 0 ? "?" : "-", sSmall);
                    GUI.Label(new Rect(x + 176, ly - 2, w - 184, 20), StageText(r), sLabel);
                }
                yy += panel.height + 4;
            }
            if (!presentation && demoLine.Length > 0) { var o = GUI.color; GUI.color = new Color(1, 1, 1, 0.8f); GUI.Label(new Rect(x, yy, w, 20), demoLine, sSmall); GUI.color = o; }
        }

        static string StageText(Req r)
        {
            switch (r.Stage)
            {
                case RS.Queued: return "your selection is queued";
                case RS.Releasing: return "releasing your ball...";
                case RS.Rolling: return "rolling to the color sensor";
                case RS.Checking: return "checking its color...";
                case RS.Detected: return r.Detected > 0 ? "it's " + Colors.Names[r.Detected].ToLowerInvariant() + "!" : "color not recognized";
                case RS.Returning: return r.Detected > 0 ? "returning to " + Colors.Names[r.Detected].ToLowerInvariant() + " storage" : "to the reject bin";
                default: return r.Stage.ToString();
            }
        }

        // ---- pendant (on screen) ----
        void DrawPendant()
        {
            float bw = Mathf.Min(120, (VW - 60) / 6f), bh = 56, total = 6 * (bw + 6), x0 = (VW - total) / 2, y0 = VH - bh - 14;
            var panel = new Rect(x0 - 8, y0 - 24, total + 10, bh + 36); GUI.Box(panel, GUIContent.none, sBox); Block(panel);
            GUI.Label(new Rect(x0, y0 - 21, total, 20), "VISITOR PENDANT - press a color    (queue " + R.Plc.VisitorQ.Count + "/" + R.Plc.VisitorQueueMax + ")", sSmall);
            for (int k = 0; k < 6; k++)
            {
                var r = new Rect(x0 + k * (bw + 6), y0, bw, bh);
                var s = new GUIStyle(sBtn) { fontSize = 16 };
                s.normal.background = swatch[k + 1]; s.hover.background = swatch[k + 1]; s.active.background = swatch[k + 1]; s.focused.background = swatch[k + 1];
                Color tc = (k == 1 || k == 4) ? Color.black : Color.white;
                s.normal.textColor = tc; s.hover.textColor = tc; s.active.textColor = tc; s.focused.textColor = tc;
                if (GUI.Button(r, Colors.Names[k + 1].ToUpperInvariant(), s)) Press(Tg.Pend[k]);
            }
        }

        // ---- operator strip: Start / Stop / E-stop / Restart / speed / startup modes + storage counts ----
        void DrawOperatorStrip()
        {
            var plc = R.Plc;
            float w = 380, x = VW - w - 10, y = 10;
            var r = new Rect(x, y, w, 138); GUI.Box(r, GUIContent.none, sBox); Block(r);
            OperatorButtons(x + 4, y + 4, 70, 28);
            if (GUI.Button(new Rect(x + 4 + 4 * 74, y + 4, 76, 28), presentation ? "Dev (P)" : "Visitor (P)", sBtn)) presentation = !presentation;
            GUI.Label(new Rect(x + 6, y + 36, 40, 20), "Speed", sSmall);
            float[] sp = { 0.5f, 1f, 2f, 4f };
            for (int i = 0; i < 4; i++)
            {
                var o = GUI.backgroundColor; if (Mathf.Abs(speed - sp[i]) < 0.01f) GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                if (GUI.Button(new Rect(x + 50 + i * 56, y + 35, 52, 22), sp[i] + "x", sBtn)) speed = sp[i];
                GUI.backgroundColor = o;
            }
            if (GUI.Button(new Rect(x + 4, y + 62, 118, 24), "Preloaded demo", sBtn)) Rebuild(Preset.PreloadedDemo, true);
            if (GUI.Button(new Rect(x + 126, y + 62, 132, 24), "Bulk startup (300)", sBtn)) Rebuild(Preset.BulkStartup, true);
            string rl = plc.Ph == Phase.SortDone || (plc.Ph == Phase.Sorting && Sim.Bulk.Count == 0) ? "Start recirculation" : plc.Ph == Phase.Recirc ? (plc.AutoEnabled ? "Auto: ON" : "Auto: OFF") : "Recirculate";
            var bo = GUI.backgroundColor; if (plc.Ph == Phase.SortDone) GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);
            if (GUI.Button(new Rect(x + 262, y + 62, 114, 24), rl, sBtn)) Press("X_Recirc_PB");
            GUI.backgroundColor = bo;
            // storage counts (stored = hopper + pump column + cup)
            for (int k = 0; k < 6; k++)
            {
                var p = Sim.Pumps[k]; int stored = Sim.Hopper[k].Count + p.Column.Count + (p.Cup != null ? 1 : 0) + (p.RampBall != null ? 1 : 0);
                float cx = x + 6 + k * 62;
                GUI.DrawTexture(new Rect(cx, y + 93, 12, 12), swatch[k + 1]);
                GUI.Label(new Rect(cx + 14, y + 89, 46, 20), stored.ToString(), sLabel);
            }
            GUI.Label(new Rect(x + 6, y + 112, w - 12, 20), "Bulk " + Sim.Bulk.Count + "  |  reject " + Sim.RejectBin.Count + "  |  " + plc.StatusText, sSmall);
        }

        void OperatorButtons(float x, float y, float w, float h)
        {
            if (GUI.Button(new Rect(x, y, w, h), "Start", sBtn)) Press("X_Start_PB");
            if (GUI.Button(new Rect(x + (w + 4), y, w, h), "Stop", sBtn)) Press("X_Stop_PB");
            var o = GUI.backgroundColor; GUI.backgroundColor = estop ? new Color(1f, 0.8f, 0.2f) : new Color(1f, 0.25f, 0.2f);
            if (GUI.Button(new Rect(x + 2 * (w + 4), y, w, h), estop ? "RELEASE" : "E-STOP*", sBtn)) { estop = !estop; R.Tags.SetBool("X_EStop_NC", !estop); }
            GUI.backgroundColor = o;
            if (GUI.Button(new Rect(x + 3 * (w + 4), y, w, h), "Restart", sBtn)) Restart();
        }

        // ---- camera buttons ----
        void DrawCameraButtons()
        {
            float bw = 150;
            var r = new Rect(10, VH - 14 - 4 * 30 - 30, bw + 8, 4 * 30 + 30); GUI.Box(r, GUIContent.none, sBox); Block(r);
            GUI.Label(new Rect(r.x + 6, r.y + 2, bw, 20), "Camera views", sSmall);
            for (int i = 0; i < 4; i++)
            {
                var o = GUI.backgroundColor; if (i == CurrentCam) GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                if (GUI.Button(new Rect(r.x + 4, r.y + 22 + i * 30, bw, 27), CamNames[i], sBtn)) Go(i);
                GUI.backgroundColor = o;
            }
        }

        // ---- developer overlay ----
        void DrawDev()
        {
            var plc = R.Plc; var st = Sim.Stats();
            var r = new Rect(10, 10, 370, VH - 190); GUI.Box(r, GUIContent.none, sBox); Block(r);
            GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 12));
            devScroll = GUILayout.BeginScrollView(devScroll);
            GUILayout.Label("<b>DEVELOPER MODE</b>   (P = visitor view)", sLabel);
            string mode = !R.Tags.GetBool("X_EStop_NC") ? "E-STOP (simulated)" : !plc.Running ? "STOPPED" : plc.Ph.ToString().ToUpperInvariant();
            GUILayout.Label("Machine: " + mode + "   " + plc.StatusText, sLabel);
            GUILayout.Label("PLC steps: " + plc.SeqText, sLabel);
            GUILayout.Label("Visitor queue " + plc.VisitorQ.Count + "/" + plc.VisitorQueueMax + "   circulating (PLC) " + plc.CircCount + " / (sim) " + Sim.OutsideStorage + "   limit " + plc.CircLimit, sLabel);
            var sb = new StringBuilder("Pumps: ");
            for (int k = 0; k < 6; k++) { var p = Sim.Pumps[k]; sb.Append(Colors.Names[k + 1][0]).Append(p.St == 0 ? (p.Cup != null ? " idle" : " ---") : p.St == 1 ? " UP" : " DN").Append(p.Div > 0.5 ? "/V " : "/A ").Append("(").Append(p.Strokes).Append(") "); }
            GUILayout.Label(sb.ToString(), sLabel);
            GUILayout.Label("Gates: stopper " + Sim.StopperOpen.ToString("F1") + " vis " + Sim.VisGateOpen.ToString("F1") + " seat " + Sim.SeatGateOpen.ToString("F1") + " pocket " + Sim.PocketGateOpen.ToString("F1") + "   chute " + Sim.ChuteAngle.ToString("F0") + " deg @ " + (Sim.ChuteIdx >= 0 ? (Sim.ChuteIdx < 6 ? Colors.Names[Sim.ChuteIdx + 1] : "reject") : "moving"), sLabel);
            GUILayout.Label("Seat color read: " + (Sim.SeatValid ? Colors.Names[Sim.SeatColor] : "-") + "   interlock hits " + Sim.InterlockHits + "   missed releases " + Sim.MissedReleases, sLabel);
            GUILayout.Label("Sorted " + plc.SortedCount + "   rejected " + plc.RejectedCount + "   visitor served " + plc.VisitorServed + "   unavailable " + plc.VisitorUnavailable + "   measured != requested " + plc.MeasuredMismatch, sLabel);
            GUILayout.Label("Inventory (true ball color):", sLabel);
            GUILayout.Label("color    total  home  misfiled  pump  bulk  circ  reject  hopper", sSmall);
            for (int c = 1; c <= 6; c++)
                GUILayout.Label(string.Format("{0,-7} {1,5} {2,5} {3,7} {4,6} {5,5} {6,5} {7,6} {8,7}", Colors.Names[c], st.Total[c], st.InOwnHopper[c], st.Misfiled[c], st.InPump[c], st.InBulk[c], st.Circulating[c], st.InReject[c], st.HopperCount[c]), sSmall);
            int expect = 6 * L.InventoryPerColor;
            var o = GUI.contentColor; GUI.contentColor = (st.Consistent && st.Grand == expect) ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.4f, 0.4f);
            GUILayout.Label("Total " + st.Grand + " / " + expect + (st.Consistent ? "   ledger OK" : "   LEDGER ERROR: " + st.Problem), sLabel);
            GUI.contentColor = o;
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Plant wrong ball", sBtn)) demoLine = Sim.PlantWrongBall();
            if (GUILayout.Button(Sim.ForceUnknownNext ? "Unknown armed" : "Force unknown", sBtn)) Sim.ForceUnknownNext = true;
            if (GUILayout.Button("Reject bin -> bulk", sBtn)) Sim.ClearRejectBin();
            GUILayout.EndHorizontal();
            speed = Slider("Sim speed x", speed, 0.25f, 4f, "F2");
            plc.AutoInterval = Slider("Auto release every s", (float)plc.AutoInterval, 0.5f, 10, "F1");
            plc.CircLimit = (int)Slider("Circulation limit (balls)", plc.CircLimit, 1, 30, "F0");
            plc.VisitorQueueMax = (int)Slider("Visitor queue limit", plc.VisitorQueueMax, 1, 8, "F0");
            ballDiameter = Mathf.Round(Slider("Ball diameter mm (restart)", ballDiameter, 16, 40, "F0"));
            inventoryPerColor = (int)Slider("Balls per color (restart)", inventoryPerColor, 14, 120, "F0");
            if (GUILayout.Button("Apply size & Restart", sBtn)) Restart();
            GUILayout.Label("Legend: blue-grey troughs = automatic path, amber = visitor path. Outlet z " + L.OutletZ.ToString("F0") + " mm, column " + L.ColumnBalls + " balls.", sSmall);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        float Slider(string label, float v, float a, float b, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + " " + v.ToString(fmt), sSmall, GUILayout.Width(190));
            v = GUILayout.HorizontalSlider(v, a, b);
            GUILayout.EndHorizontal();
            return v;
        }
    }
}
