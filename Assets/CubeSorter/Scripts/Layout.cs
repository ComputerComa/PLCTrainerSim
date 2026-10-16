using System;
using System.Collections.Generic;
using System.Text;

namespace CubeSorter
{
    /// <summary>
    /// Single source of truth for the revision-2 ball-sorter geometry (Blender frame, millimetres).
    /// X = right as seen by the visitor, Y = away from the visitor, Z = up. The inspection tower (seat, holding pocket, rotary chute) is on the X=0, Y=0..190 centre line.
    /// Two banks of three storage hoppers (colors 1-3 on the left, 4-6 on the right) have their pumps on the OUTER ends; the shared bulk hopper sits behind the tower.
    /// Every rolling path is built with z falling linearly along its length, so there is no uphill section or low spot by construction (Validate() checks it).
    /// Blender scripts/cs_build_balls.py reads layout.json exported from here.
    /// </summary>
    public class Layout
    {
        public const int N = 6;

        // ---------- design inputs (configurable) ----------
        public double BallD = 20.0;
        public int InventoryPerColor = 50;
        public int ColumnBalls = 20;        // balls held in each pump column when primed
        public int HopperCols = 4;
        public double HopperGap = 8, InnerEdge = 260, FloorTiltDeg = 12;
        public double ZfRear = 78, ZCup = 70;
        public double ChuteZ = 240, StationR = 60, TubeInletZ = 0 /*0 = hopper rim + 22*/;
        public double BulkR = 110, BulkY = 390, BulkExitZ = 340;
        public int RejectCap = 12, BulkCap = 330;
        public double RejectBinY = -340, RejectBinZ = 150;

        // ---------- derived ----------
        public double Rb, Cell, HopperInnerW, HopperW, HopperPitch, HopperDepth, HopperRimZ, YRear, YFront, Tilt;
        public int HopperRows;
        public double[] HopperX = new double[N], HopperY = new double[N], Side = new double[N];
        public double[] PumpX = new double[N];
        public double OutletZ, TubeInnerR, TubeOuterR, TubeInZ;
        public double BulkRimZ, BulkWallZ;
        public double ZPocket, ZSeat, ZJunction, SeatY = 70, JunctionY = 190, StopperY = 235;
        public double[] StationAngle = new double[7];       // deg, 0..5 hoppers, 6 reject
        public V3[] Station = new V3[7];
        public V3[] Inlet = new V3[7];
        public Path[] Ramp = new Path[N], OutPath = new Path[N], AutoPath = new Path[N], VisPath = new Path[N];
        public Path[] ChuteArm = new Path[7], Tube = new Path[7];
        public Path AutoFeed, VisJoinL, VisJoinR, SeatToPocket;
        public double StopperS, VisWaitS, JunctionS;                   // arc length of the automatic stopper on AutoFeed / of the visitor gate on VisJoin
        public double MinX, MaxX, MinY, MaxY;
        int[] colOrder;

        public Layout() { Build(); }

        static Path LinearZ(double z0, double z1, params double[] xy)
        {
            int n = xy.Length / 2; double[] cum = new double[n];
            for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Math.Sqrt((xy[2 * i] - xy[2 * i - 2]) * (xy[2 * i] - xy[2 * i - 2]) + (xy[2 * i + 1] - xy[2 * i - 1]) * (xy[2 * i + 1] - xy[2 * i - 1]));
            var pts = new V3[n];
            for (int i = 0; i < n; i++) pts[i] = new V3(xy[2 * i], xy[2 * i + 1], z0 + (z1 - z0) * (cum[n - 1] < 1e-9 ? 0 : cum[i] / cum[n - 1]));
            return new Path(pts);
        }

        static Path Bezier(V3 a, V3 b, V3 c, V3 d, double z0, double z1, int n = 28)
        {
            var xy = new double[2 * (n + 1)];
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n, u = 1 - t;
                xy[2 * i] = u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X;
                xy[2 * i + 1] = u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y;
            }
            return LinearZ(z0, z1, xy);
        }

        public void Build()
        {
            Rb = BallD / 2; Cell = BallD + 2; HopperInnerW = HopperCols * Cell;
            double wall = 3; HopperW = HopperInnerW + 2 * wall; HopperPitch = HopperW + HopperGap;
            Tilt = Math.Tan(FloorTiltDeg * Math.PI / 180);
            HopperRows = Math.Max(5, (int)Math.Ceiling((InventoryPerColor + 8) / (2.0 * HopperCols)));
            HopperDepth = HopperRows * Cell + 2 * wall;
            YRear = HopperDepth / 2 - wall - Cell / 2;
            YFront = YRear - (HopperRows - 1) * Cell;
            double zfFront = ZfRear + (YRear - YFront) * Tilt;
            HopperRimZ = zfFront + 0.85 * Cell + Rb + 2;
            TubeInZ = TubeInletZ > 0 ? TubeInletZ : HopperRimZ + 22;
            TubeInnerR = Rb + 1.5; TubeOuterR = TubeInnerR + 1.5;

            colOrder = new int[HopperCols];
            var cols = new List<int>(); for (int c = 0; c < HopperCols; c++) cols.Add(c);
            double mid = (HopperCols - 1) / 2.0;
            cols.Sort((a, b) => { double da = Math.Abs(a - mid), db = Math.Abs(b - mid); int r = da.CompareTo(db); return r != 0 ? r : a.CompareTo(b); });
            colOrder = cols.ToArray();

            OutletZ = ZCup + (ColumnBalls + 1) * BallD;
            for (int k = 0; k < N; k++)
            {
                int bank = k / 3, j = k % 3;
                Side[k] = bank == 0 ? -1 : 1;
                HopperY[k] = (j - 1) * HopperPitch;
                HopperX[k] = Side[k] * (InnerEdge + HopperDepth / 2);
                PumpX[k] = Side[k] * (InnerEdge + HopperDepth + Rb + 14);
            }
            // vertical stack on the centre line, from the bottom up
            ZPocket = ChuteZ + 34;
            ZSeat = ZPocket + 24;
            BulkRimZ = BulkExitZ + 70; BulkWallZ = BulkRimZ + 10;

            // rotary chute stations (6 hoppers + reject at the front)
            double[] ang = { 150, 180, 210, -30, 0, 30 };
            // left bank: front (Y<0) hopper gets the lower-Y station
            ang[0] = 210; ang[1] = 180; ang[2] = 150; ang[3] = -30; ang[4] = 0; ang[5] = 30;
            for (int k = 0; k < N; k++) StationAngle[k] = ang[k];
            StationAngle[6] = -90;
            for (int i = 0; i < 7; i++)
            {
                double a = StationAngle[i] * Math.PI / 180;
                Station[i] = new V3(StationR * Math.Cos(a), StationR * Math.Sin(a), ChuteZ);
                ChuteArm[i] = new Path(new V3(0, 0, ZPocket), new V3(0, 0, ChuteZ + 16), Station[i]);
            }
            for (int i = 0; i < N; i++)
            {
                Inlet[i] = new V3(Side[i] * (InnerEdge + 18), HopperY[i], TubeInZ);
                double a = StationAngle[i] * Math.PI / 180; var s = Station[i];
                V3 p1 = new V3(s.X + 70 * Math.Cos(a), s.Y + 70 * Math.Sin(a), 0), p2 = new V3(Inlet[i].X - Side[i] * 90, Inlet[i].Y, 0);
                Tube[i] = Bezier(s, p1, p2, Inlet[i], ChuteZ, TubeInZ);
            }
            Inlet[6] = new V3(0, RejectBinY, RejectBinZ);
            Tube[6] = Bezier(Station[6], new V3(0, -StationR - 70, 0), new V3(0, RejectBinY + 90, 0), Inlet[6], ChuteZ, RejectBinZ);

            AutoFeed = LinearZ(BulkExitZ, ZSeat, 0, BulkY, 0, StopperY + 30, 0, StopperY, 0, JunctionY, 0, SeatY);
            JunctionS = V3.Dist(new V3(0, BulkY, 0), new V3(0, JunctionY, 0));
            ZJunction = AutoFeed.At(JunctionS).Z;
            for (int k = 0; k < N; k++)
            {
                double sx = Side[k], x = HopperX[k], hy = HopperY[k];
                Ramp[k] = new Path(Slot(k, 0), new V3(x + sx * (HopperDepth / 2 + 2), hy, ZfRear - 3), Cup(k));
                var o = Outlet(k);
                var dv = Diverter(k);
                OutPath[k] = new Path(o, dv);
                // automatic collector: diverter -> loading funnel over the bulk hopper
                double bx = sx * 55, by = BulkY + (k % 3 - 1) * 40;
                AutoPath[k] = LinearZ(dv.Z, BulkRimZ + 28, dv.X, dv.Y, sx * 300, (dv.Y + by) / 2 + (k % 3 - 1) * 10, bx, by);
                // visitor collector: diverter -> bank visitor funnel -> shared visitor rail -> junction
                double vbX = sx * 120;
                VisPath[k] = LinearZ(dv.Z, ZJunction + 18, dv.X, dv.Y, sx * 250, (dv.Y + JunctionY) / 2, vbX, JunctionY);
            }
            StopperS = V3.Dist(new V3(0, BulkY, 0), new V3(0, StopperY, 0));
            VisJoinL = LinearZ(ZJunction + 18, ZJunction, -120, JunctionY, -70, JunctionY, 0, JunctionY);
            VisJoinR = LinearZ(ZJunction + 18, ZJunction, 120, JunctionY, 70, JunctionY, 0, JunctionY);
            VisWaitS = 50;
            SeatToPocket = new Path(new V3(0, SeatY, ZSeat), new V3(0, 0, ZPocket));

            MaxX = Math.Abs(PumpX[0]) + 70; MinX = -MaxX; MinY = RejectBinY - 70; MaxY = BulkY + BulkR + 40;
        }

        /// <summary>Ball released at the pump outlet (top of the column).</summary>
        public V3 Outlet(int k) { return new V3(PumpX[k], HopperY[k], OutletZ); }
        /// <summary>Selectable diverter flap, just inboard of the outlet.</summary>
        public V3 Diverter(int k) { return new V3(PumpX[k] - Side[k] * 30, HopperY[k], OutletZ - 10); }

        /// <summary>Position of the idx-th ball stored in hopper h (idx 0 sits at the exit port and is fed first). Hopper local: u across, v toward the pump.</summary>
        public V3 Slot(int h, int idx)
        {
            int per = HopperCols * HopperRows;
            int l = idx / per, rem = idx % per, r = rem / HopperCols, cc = colOrder[rem % HopperCols];
            double u = (cc - (HopperCols - 1) / 2.0) * Cell;
            double v = YRear - r * Cell - ((l & 1) == 1 ? Cell * 0.5 : 0);
            double z = ZfRear + (YRear - v) * Tilt + l * 0.85 * Cell;
            return new V3(HopperX[h] + Side[h] * v, HopperY[h] + u, z);
        }

        public V3 Cup(int k) { return new V3(PumpX[k], HopperY[k], ZCup); }
        public V3 ColumnRest(int k, int i) { return new V3(PumpX[k], HopperY[k], ZCup + (i + 1) * BallD); }

        /// <summary>Resting place of the n-th ball in the bulk hopper (arranged pile, centre exit).</summary>
        public V3 BulkSlot(int idx)
        {
            // hexagonal rings out from the exit, layers stacked; cone floor rises with radius
            double cone = Math.Tan(12 * Math.PI / 180);
            int placed = 0; int layer = 0;
            while (true)
            {
                int ringMax = (int)((BulkR - Rb - 4) / (BallD * 0.95));
                for (int ring = 0; ring <= ringMax; ring++)
                {
                    int cnt = ring == 0 ? 1 : 6 * ring;
                    if (idx < placed + cnt)
                    {
                        int i = idx - placed; double r = ring * BallD * 0.95, a = (ring == 0 ? 0 : 2 * Math.PI * i / cnt) + (layer & 1) * 0.5;
                        return new V3(r * Math.Cos(a), BulkY + r * Math.Sin(a), BulkExitZ + 12 + r * cone + layer * 0.82 * BallD);
                    }
                    placed += cnt;
                }
                layer++;
            }
        }

        /// <summary>Reject bin (front, in the centre): 3 balls wide.</summary>
        public V3 RejectSlot(int idx)
        {
            int cols = 3, rows = Math.Max(1, RejectCap / cols), per = cols * rows;
            int l = idx / per, rem = idx % per, r = rem / cols, c = rem % cols;
            return new V3((c - 1) * Cell, RejectBinY - (rows - 1) * Cell / 2.0 + r * Cell, RejectBinZ - 40 + l * 0.85 * Cell);
        }

        static double SlopeDeg(V3 a, V3 b)
        {
            double h = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            return Math.Atan2(a.Z - b.Z, h) * 180 / Math.PI;
        }
        /// <summary>Minimum segment slope (degrees, positive = downhill in the rolling direction) and the largest.</summary>
        public static void SlopeRange(Path p, out double min, out double max)
        {
            min = 1e9; max = -1e9;
            for (int i = 1; i < p.P.Length; i++)
            {
                if (V3.Dist(new V3(p.P[i].X, p.P[i].Y, 0), new V3(p.P[i - 1].X, p.P[i - 1].Y, 0)) < 1e-6) continue;   // vertical drop
                double s = SlopeDeg(p.P[i - 1], p.P[i]); min = Math.Min(min, s); max = Math.Max(max, s);
            }
            if (min > 1e8) { min = max = 90; }
        }

        /// <summary>Every gravity route must fall continuously: no uphill section and no low spot (min slope above the threshold).</summary>
        public List<string> Validate(double minDeg = 2.5)
        {
            var bad = new List<string>();
            Action<string, Path> chk = (n, p) => { double a, b; SlopeRange(p, out a, out b); if (a < minDeg) bad.Add(n + " min slope " + a.ToString("F1") + " deg"); };
            for (int k = 0; k < N; k++) { chk("AutoPath" + k, AutoPath[k]); chk("VisPath" + k, VisPath[k]); chk("Tube" + k, Tube[k]); chk("ChuteArm" + k, ChuteArm[k]); chk("Ramp" + k, Ramp[k]); }
            chk("Tube6", Tube[6]); chk("ChuteArm6", ChuteArm[6]); chk("AutoFeed", AutoFeed); chk("VisJoinL", VisJoinL); chk("VisJoinR", VisJoinR); chk("SeatToPocket", SeatToPocket);
            if (BulkRimZ + 14 >= OutletZ - 10) bad.Add("bulk hopper rim is above the pump outlets");
            return bad;
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("ball {0} mm, {1}/color, column {2} balls (outlet z {3:F0}), hopper {4}x{5} cells ({6:F0} x {7:F0} mm), rim z {8:F0}\n", BallD, InventoryPerColor, ColumnBalls, OutletZ, HopperCols, HopperRows, HopperW, HopperDepth, HopperRimZ);
            sb.AppendFormat("stack z: junction {0:F0}  seat {1:F0}  pocket {2:F0}  chute stations {3:F0}  tube inlets {4:F0}  bulk exit {5:F0}  bulk rim {6:F0}\n", ZJunction, ZSeat, ZPocket, ChuteZ, TubeInZ, BulkExitZ, BulkRimZ);
            double a, b;
            for (int k = 0; k < N; k++)
            {
                double a1, b1, a2, b2, a3, b3; SlopeRange(AutoPath[k], out a1, out b1); SlopeRange(VisPath[k], out a2, out b2); SlopeRange(Tube[k], out a3, out b3);
                sb.AppendFormat("  {0,-6} auto {1:F0} mm {2:F1} deg | visitor {3:F0} mm {4:F1} deg | return tube {5:F0} mm {6:F1} deg\n", Colors.Names[k + 1], AutoPath[k].Length, a1, VisPath[k].Length, a2, Tube[k].Length, a3);
            }
            SlopeRange(Tube[6], out a, out b); sb.AppendFormat("  reject tube {0:F0} mm {1:F1} deg\n", Tube[6].Length, a);
            SlopeRange(AutoFeed, out a, out b); sb.AppendFormat("  auto feed {0:F0} mm {1:F1}..{2:F1} deg\n", AutoFeed.Length, a, b);
            var bad = Validate(); sb.AppendLine(bad.Count == 0 ? "  all gravity routes fall continuously" : "  PROBLEMS: " + string.Join("; ", bad.ToArray()));
            sb.AppendFormat("  footprint x {0:F0}..{1:F0}  y {2:F0}..{3:F0}", MinX, MaxX, MinY, MaxY);
            return sb.ToString();
        }
    }
}
