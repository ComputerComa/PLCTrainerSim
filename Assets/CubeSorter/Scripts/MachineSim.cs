using System;
using System.Collections.Generic;

namespace CubeSorter
{
    public class Pump
    {
        public readonly List<Ball> Column = new List<Ball>(); // index 0 = lowest ball in the column
        public Ball Cup;          // ball sitting on the piston head at the inlet
        public Ball RampBall;     // ball rolling from the hopper to the cup
        public Ball Out;          // ball between the column top and the diverter flap (outlet zone)
        public int St;            // 0 idle, 1 piston rising, 2 piston falling
        public double T;          // time in the current stroke phase
        public double Piston;     // 0 = retracted .. 1 = raised one ball pitch (visual + ball motion)
        public double Pawl = 1;   // 1 = retaining pawls engaged under the column, 0 = retracted
        public double Div;        // diverter flap: 0 = automatic path, 1 = visitor path (animated)
        public bool DivCmd;       // commanded position (true = visitor)
        public bool Cmd, Busy, Done, Released, Fault;
        public int ReqId;
        public int Strokes, Primes;
    }

    public class InventoryStats
    {
        public int[] Total = new int[7];        // balls of each TRUE color (index 1..6)
        public int[] InOwnHopper = new int[7];
        public int[] Misfiled = new int[7];
        public int[] InPump = new int[7];       // pump column + cup + ramp + outlet
        public int[] InBulk = new int[7];
        public int[] Circulating = new int[7];  // collectors, feeder, seat, pocket, chute, return tubes
        public int[] InReject = new int[7];
        public int[] HopperCount = new int[7];
        public int[] ColumnCount = new int[7];
        public int Grand;
        public bool Consistent; public string Problem = "";
    }

    public enum Preset { PreloadedDemo, BulkStartup }

    /// <summary>
    /// Plain-C# model of the revision-2 machine: bulk hopper, six storage hoppers with GBC-style piston pumps and outlet diverters, automatic and visitor feed paths
    /// (separate until the junction, automatic stopper), a stationary colour seat above a recessed sensor, a controlled gate into a holding pocket, a stepper-driven rotary chute
    /// and six return tubes plus a reject bin. Ball movement is deterministic path following with occupancy-controlled queues (no rigid-body physics).
    /// Every ball lives in exactly one container. Positions are Blender-frame millimetres (see Layout).
    /// </summary>
    public class MachineSim : IMachineIO
    {
        public readonly Layout L;
        public readonly List<Ball> Balls = new List<Ball>();
        public readonly List<Ball>[] Hopper = new List<Ball>[Layout.N];
        public readonly Pump[] Pumps = new Pump[Layout.N];
        public readonly List<Ball>[] AutoRoll = new List<Ball>[Layout.N];
        public readonly List<Ball>[] VisRoll = new List<Ball>[Layout.N];
        public readonly List<Ball> Bulk = new List<Ball>();
        public readonly List<Ball> Feed = new List<Ball>();        // balls rolling on AutoFeed (front of the list = closest to the seat)
        public readonly List<Ball> Return = new List<Ball>();      // balls on the chute arm / return tubes
        public readonly List<Ball> RejectBin = new List<Ball>();
        public Ball Seat, ToPocket, Pocket;
        public Path[] Comb = new Path[7]; public double[] ArmLen = new double[7];
        public Path[] VisFull = new Path[Layout.N];

        // ---- tunables ----
        public double RiseT = 0.9, FallT = 0.7;
        public double RampV = 150, Accel = 700, FeedV = 220, VisV = 260, AutoV = 240, ReturnV = 300, SeatV = 160, PocketV = 150;
        public double ColorDwell = 0.6, StoredSpeed = 500, GateT = 0.18, DivT = 0.25, ChuteSpeed = 220;
        public double Spacing { get { return L.BallD + 0.6; } }

        // ---- machine state visible to the view ----
        public double StopperOpen, VisGateOpen, SeatGateOpen, PocketGateOpen;     // 0 closed .. 1 open (visual + physical)
        public double ChuteAngle; public int ChuteIdx;                            // current physical station (0..6) once in position
        public int ChuteTarget = -1;
        public bool ColorLightOn;
        public bool SeatValid; public int SeatColor;
        public bool ForceUnknownNext;
        public int UnknownReads, InterlockHits, MissedReleases;
        public double Time;

        // ---- PLC outputs latched from tags ----
        bool motionEn, stopperCmd, visGateCmd, seatGateCmd, pocketCmd; int chuteCmd;
        double seatDwell;
        readonly Random rng;
        public Preset Current = Preset.PreloadedDemo;

        public MachineSim(Layout layout, int seed, Preset preset = Preset.PreloadedDemo)
        {
            L = layout; rng = new Random(seed);
            for (int k = 0; k < Layout.N; k++) { Hopper[k] = new List<Ball>(); AutoRoll[k] = new List<Ball>(); VisRoll[k] = new List<Ball>(); Pumps[k] = new Pump(); }
            for (int i = 0; i < 7; i++)
            {
                var pts = new List<V3>(L.ChuteArm[i].P); for (int j = 1; j < L.Tube[i].P.Length; j++) pts.Add(L.Tube[i].P[j]);
                Comb[i] = new Path(pts.ToArray()); ArmLen[i] = L.ChuteArm[i].Length;
            }
            for (int k = 0; k < Layout.N; k++)
            {
                var vj = L.Side[k] < 0 ? L.VisJoinL : L.VisJoinR; var pts = new List<V3>(L.VisPath[k].P); for (int j = 1; j < vj.P.Length; j++) pts.Add(vj.P[j]);
                VisFull[k] = new Path(pts.ToArray());
            }
            Reset(preset);
        }

        public void Reset(Preset preset)
        {
            Current = preset;
            Balls.Clear(); Bulk.Clear(); Feed.Clear(); Return.Clear(); RejectBin.Clear(); Seat = ToPocket = Pocket = null;
            for (int k = 0; k < Layout.N; k++) { Hopper[k].Clear(); AutoRoll[k].Clear(); VisRoll[k].Clear(); Pumps[k] = new Pump(); }
            StopperOpen = VisGateOpen = SeatGateOpen = PocketGateOpen = 0; stopperCmd = visGateCmd = seatGateCmd = pocketCmd = false; chuteCmd = 0;
            ChuteIdx = 6; ChuteAngle = L.StationAngle[6]; ChuteTarget = -1;
            Time = 0; UnknownReads = 0; InterlockHits = 0; MissedReleases = 0; ForceUnknownNext = false; SeatValid = false; SeatColor = 0; seatDwell = 0; ColorLightOn = false;
            int id = 0, per = L.InventoryPerColor;
            var all = new List<int>();
            for (int c = 1; c <= Layout.N; c++) for (int i = 0; i < per; i++) all.Add(c);
            if (preset == Preset.BulkStartup)
            {
                for (int i = all.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int t = all[i]; all[i] = all[j]; all[j] = t; }   // mixed batch
                foreach (int c in all) { var b = new Ball { Id = id++, TrueColor = c, Teleport = true, Loc = BallLoc.Bulk, Where = -1 }; Balls.Add(b); b.Pos = L.BulkSlot(Bulk.Count); Bulk.Add(b); }
            }
            else
            {
                for (int c = 1; c <= Layout.N; c++)
                {
                    int h = c - 1; var pump = Pumps[h];
                    for (int i = 0; i < per; i++)
                    {
                        var b = new Ball { Id = id++, TrueColor = c, Teleport = true }; Balls.Add(b);
                        if (pump.Column.Count < L.ColumnBalls) { b.Loc = BallLoc.Pump; b.Where = h; b.Pos = L.ColumnRest(h, pump.Column.Count); pump.Column.Add(b); }
                        else if (pump.Cup == null) { b.Loc = BallLoc.Pump; b.Where = h; b.Pos = L.Cup(h); pump.Cup = b; }
                        else { b.Loc = BallLoc.Hopper; b.Where = h; b.Pos = L.Slot(h, Hopper[h].Count); Hopper[h].Add(b); }
                    }
                }
            }
        }

        // ================= tag exchange =================
        double VisGateS(int k) { return L.VisPath[k].Length + L.VisWaitS; }

        public void ReadInputs(TagTable t)
        {
            bool anyAuto = false, anyVis = false, visAtGate = false, visPastGate = false;
            for (int k = 0; k < Layout.N; k++)
            {
                var p = Pumps[k];
                t.SetBool(Tg.PumpCup[k], p.Cup != null);
                t.SetBool(Tg.PumpColFull[k], p.Column.Count >= L.ColumnBalls);
                t.SetBool(Tg.PumpHopEmpty[k], Hopper[k].Count == 0 && p.RampBall == null);
                t.SetBool(Tg.PumpBusy[k], p.Busy);
                t.SetBool(Tg.PumpDone[k], p.Done);
                t.SetBool(Tg.PumpReleased[k], p.Released); p.Released = false;
                t.SetBool(Tg.PumpFault[k], p.Fault);
                t.SetBool(Tg.DivAtAuto[k], p.Div < 0.02);
                t.SetBool(Tg.DivAtVis[k], p.Div > 0.98);
                t.SetBool(Tg.OutBall[k], p.Out != null);
                if (AutoRoll[k].Count > 0) anyAuto = true;
                foreach (var b in VisRoll[k]) { anyVis = true; if (b.S >= VisGateS(k) - 0.8 && b.S <= VisGateS(k) + 0.8) visAtGate = true; if (b.S > VisGateS(k) + 0.8) visPastGate = true; }
                if (p.Out != null && p.Div > 0.5) anyVis = true;
            }
            bool stopperBall = false, pastStopper = false;
            foreach (var b in Feed) { if (b.S >= L.StopperS - 0.8 && b.S <= L.StopperS + 0.8) stopperBall = true; if (b.S > L.StopperS + 0.8) pastStopper = true; }
            t.SetBool("X_AutoColl_Ball", anyAuto);
            t.SetBool("X_VisPath_Ball", anyVis);
            t.SetBool("X_VisGate_Ball", visAtGate);
            t.SetBool("X_AutoStopper_Ball", stopperBall);
            t.SetBool("X_Feeder_Clear", !pastStopper && !visPastGate);
            t.SetInt("DS_Bulk_Count", Bulk.Count);
            t.SetBool("X_Bulk_Empty", Bulk.Count == 0);
            t.SetBool("X_Seat_Ball", Seat != null);
            t.SetBool("X_Color_Valid", Seat != null && SeatValid);
            t.SetInt("DS_Color_Detected", Seat != null && SeatValid ? SeatColor : 0);
            t.SetBool("X_Pocket_Ball", Pocket != null);
            t.SetBool("X_Transfer_Busy", ToPocket != null);
            t.SetBool("X_Chute_InPos", ChuteIdx >= 0 && chuteCmd > 0 && ChuteIdx == ChuteStation(chuteCmd));
            t.SetInt("DS_Chute_Pos", ChuteIdx >= 0 ? ChuteIdx + 1 : 0);
            t.SetBool("X_Chute_Clear", !ArmBusy());
            t.SetBool("X_Reject_Full", RejectBin.Count >= L.RejectCap);
            t.SetBool("X_Stopper_Closed", StopperOpen < 0.02);
            t.SetBool("X_VisGate_Closed", VisGateOpen < 0.02);
            t.SetBool("X_SeatGate_Closed", SeatGateOpen < 0.02);
            t.SetBool("X_PocketGate_Closed", PocketGateOpen < 0.02);
        }

        public void WriteOutputs(TagTable t)
        {
            motionEn = t.GetBool("Y_Motion_Enable");
            stopperCmd = t.GetBool("Y_AutoStopper_Open");
            visGateCmd = t.GetBool("Y_VisGate_Open");
            seatGateCmd = t.GetBool("Y_SeatGate_Open");
            pocketCmd = t.GetBool("Y_PocketGate_Open");
            ColorLightOn = t.GetBool("Y_Color_Light");
            chuteCmd = t.GetInt("DS_Chute_Cmd");
            for (int k = 0; k < Layout.N; k++)
            {
                var p = Pumps[k];
                bool cmd = t.GetBool(Tg.PumpCycle[k]);
                if (cmd && !p.Cmd) p.ReqId = t.GetInt(Tg.PumpReqId[k]);
                p.Cmd = cmd; p.DivCmd = t.GetBool(Tg.DivVisitor[k]);
            }
        }

        static int ChuteStation(int cmd) { return cmd >= 7 ? 6 : cmd - 1; }

        public bool ArmBusy()
        {
            foreach (var b in Return) if (b.S < ArmLen[b.Where] + L.BallD) return true;
            return false;
        }

        // ================= simulation step =================
        public void Step(double dt)
        {
            Time += dt;
            for (int k = 0; k < Layout.N; k++) UpdatePump(k, dt);
            for (int k = 0; k < Layout.N; k++) { MoveAuto(k, dt); MoveVisitor(k, dt); }
            MoveBulkExit(); MoveFeed(dt);
            UpdateSeat(dt); MovePocket(dt); ReleasePocket(); MoveReturn(dt); UpdateChute(dt);
            StopperOpen = Approach(StopperOpen, stopperCmd ? 1 : 0, dt / GateT);
            VisGateOpen = Approach(VisGateOpen, visGateCmd ? 1 : 0, dt / GateT);
            SeatGateOpen = Approach(SeatGateOpen, seatGateCmd ? 1 : 0, dt / GateT);
            PocketGateOpen = Approach(PocketGateOpen, pocketCmd ? 1 : 0, dt / GateT);
            UpdateStored(dt);
        }

        static double Approach(double v, double target, double step) { return v < target ? Math.Min(target, v + step) : Math.Max(target, v - step); }
        static double Ease(double f) { f = Math.Max(0, Math.Min(1, f)); return f * f * (3 - 2 * f); }

        // ---- rolling along a path with braking toward limitS ----
        void Roll(Ball b, double limitS, double vmax, double dt)
        {
            double dist = limitS - b.S;
            if (dist < 0.02) { b.S = Math.Max(b.S, limitS); b.V = 0; return; }
            double vAllow = Math.Sqrt(2 * Accel * dist);
            double tgt = Math.Min(vmax, vAllow);
            b.V = b.V < tgt ? Math.Min(tgt, b.V + Accel * dt) : tgt;
            double step = b.V * dt;
            if (step >= dist) { b.S = limitS; b.V = 0; } else b.S += step;
        }

        // ================= pumps + outlet diverters =================
        void UpdatePump(int k, double dt)
        {
            var p = Pumps[k];
            if (!p.Cmd) { p.Done = false; p.Fault = false; }
            // diverter flap: moves only with power and only while no ball is in the outlet zone
            double divTarget = p.DivCmd ? 1 : 0;
            if (motionEn && p.Out == null) p.Div = Approach(p.Div, divTarget, dt / DivT);
            bool divReady = Math.Abs(p.Div - divTarget) < 0.01;
            if (p.St == 0 && p.Cmd && !p.Done && motionEn && p.Out == null && divReady)
            {
                if (p.Cup != null) { p.St = 1; p.T = 0; p.Busy = true; }
                else p.Fault = true; // commanded a stroke with nothing on the piston
            }
            if (motionEn)
            {
                if (p.St == 1)
                {
                    p.T += dt; double f = Math.Min(1, p.T / RiseT);
                    p.Piston = Ease(f);
                    p.Pawl = f < 0.05 ? 1 : f < 0.15 ? 1 - (f - 0.05) / 0.10 : f < 0.80 ? 0 : f < 0.95 ? (f - 0.80) / 0.15 : 1;
                    if (p.T >= RiseT) { CompleteRise(k); p.St = 2; p.T = 0; p.Piston = 1; p.Pawl = 1; }
                }
                else if (p.St == 2)
                {
                    p.T += dt; p.Piston = 1 - Ease(p.T / FallT);
                    if (p.T >= FallT) { p.St = 0; p.Piston = 0; p.Busy = false; p.Done = true; p.Strokes++; }
                }
            }
            // gravity feed: hopper -> ramp -> cup, only while the piston is home
            if (p.St == 0 && p.Cup == null && p.RampBall == null && Hopper[k].Count > 0)
            {
                var b = Hopper[k][0]; Hopper[k].RemoveAt(0);
                b.Loc = BallLoc.Pump; b.Where = k; b.P = L.Ramp[k]; b.S = 0; b.V = 0; p.RampBall = b;
            }
            if (p.RampBall != null)
            {
                var b = p.RampBall; Roll(b, b.P.Length, RampV, dt);
                b.Pos = b.P.At(b.S);
                if (b.S >= b.P.Length - 0.02) { p.Cup = b; p.RampBall = null; b.P = null; b.Pos = L.Cup(k); }
            }
            // column / cup positions (exact)
            double lift = p.St == 1 ? p.Piston * L.BallD : 0;
            for (int i = 0; i < p.Column.Count; i++) { var c = p.Column[i]; c.Pos = L.ColumnRest(k, i) + new V3(0, 0, lift); }
            if (p.Cup != null) p.Cup.Pos = L.Cup(k) + new V3(0, 0, lift);
            // outlet zone: the released ball rolls to the diverter flap, which sends it down the automatic or the visitor collector
            if (p.Out != null)
            {
                var b = p.Out; double Lo = b.P.Length;
                bool settled = p.Div < 0.02 || p.Div > 0.98;
                Roll(b, settled ? Lo : Lo - 0.01, RampV, dt); b.Pos = b.P.At(b.S);
                if (b.S >= Lo - 0.02 && settled)
                {
                    p.Out = null; b.V = 0; b.S = 0; b.Loc = BallLoc.Collect;
                    if (p.Div < 0.5) { b.P = L.AutoPath[k]; AutoRoll[k].Add(b); }
                    else { b.P = VisFull[k]; VisRoll[k].Add(b); }
                }
            }
        }

        void CompleteRise(int k)
        {
            var p = Pumps[k];
            var c = p.Cup; p.Cup = null;
            p.Column.Insert(0, c); c.Loc = BallLoc.Pump; c.Where = k;
            if (p.Column.Count > L.ColumnBalls)
            {
                var top = p.Column[p.Column.Count - 1]; p.Column.RemoveAt(p.Column.Count - 1);
                top.Loc = BallLoc.Collect; top.Where = k; top.P = L.OutPath[k]; top.S = 0; top.V = 0; top.Pos = L.Outlet(k); top.ReqId = p.ReqId;
                p.Out = top; p.Released = true;
            }
            else p.Primes++;
        }

        // ================= collectors, bulk hopper, feeder =================
        void MoveAuto(int k, double dt)
        {
            var q = AutoRoll[k];
            for (int j = 0; j < q.Count; j++)
            {
                var b = q[j]; double Lp = b.P.Length;
                double limit = j == 0 ? Lp + 5000 : q[j - 1].S - Spacing;
                Roll(b, limit, AutoV, dt);
                if (b.S >= Lp)
                {
                    q.RemoveAt(j); j--;
                    b.P = null; b.Loc = BallLoc.Bulk; b.Where = -1; b.S = 0; b.V = 0; b.Pos = b.P == null ? L.AutoPath[k].P[L.AutoPath[k].P.Length - 1] : b.Pos; Bulk.Add(b);
                    continue;
                }
                b.Pos = b.P.At(b.S);
            }
        }

        void MoveVisitor(int k, double dt)
        {
            var q = VisRoll[k];
            for (int j = 0; j < q.Count; j++)
            {
                var b = q[j]; double Lp = b.P.Length, gate = VisGateS(k);
                double limit = j == 0 ? ((visGateCmd && VisGateOpen > 0.7) || b.S > gate + 0.5 ? Lp : gate) : q[j - 1].S - Spacing;
                // do not run onto a ball that has just entered the shared feeder at the junction
                if (limit >= Lp) foreach (var f in Feed) if (Math.Abs(f.S - L.JunctionS) < Spacing) limit = Math.Min(limit, Lp - 0.5);
                Roll(b, limit, VisV, dt);
                if (b.S >= Lp - 0.02 && limit >= Lp)
                {
                    q.RemoveAt(j); j--;
                    b.P = L.AutoFeed; b.S = L.JunctionS; b.V = 0; b.Loc = BallLoc.Feed; InsertFeed(b);
                    continue;
                }
                b.Pos = b.P.At(b.S);
            }
        }

        void InsertFeed(Ball b)
        {
            int i = 0; while (i < Feed.Count && Feed[i].S > b.S) i++;
            Feed.Insert(i, b); b.Pos = L.AutoFeed.At(b.S);
        }

        /// <summary>Gravity from the bulk hopper exit: a ball enters the automatic feed whenever there is room behind the stopper (no PLC action).</summary>
        void MoveBulkExit()
        {
            if (Bulk.Count == 0) return;
            if (Feed.Count > 0 && Feed[Feed.Count - 1].S < 1.6 * Spacing) return;
            int idx = rng.Next(Math.Min(Bulk.Count, 30));
            var b = Bulk[idx]; Bulk.RemoveAt(idx);
            b.Loc = BallLoc.Feed; b.P = L.AutoFeed; b.S = 0; b.V = 0; b.Pos = b.P.At(0); b.Teleport = false;
            Feed.Add(b);
        }

        void MoveFeed(double dt)
        {
            double Lf = L.AutoFeed.Length;
            for (int j = 0; j < Feed.Count; j++)
            {
                var b = Feed[j]; double limit;
                if (j == 0) limit = Seat == null ? Lf : Lf - Spacing; else limit = Feed[j - 1].S - Spacing;
                if (b.S <= L.StopperS + 0.5 && StopperOpen < 0.7) limit = Math.Min(limit, L.StopperS);
                Roll(b, limit, FeedV, dt);
                if (j == 0 && b.S >= Lf - 0.02 && Seat == null)
                {
                    Feed.RemoveAt(0); j--;
                    Seat = b; b.Loc = BallLoc.Seat; b.Where = -1; b.P = null; b.V = 0; b.Pos = new V3(0, L.SeatY, L.ZSeat);
                    seatDwell = 0; SeatValid = false; SeatColor = 0;
                    continue;
                }
                b.Pos = b.P.At(b.S);
            }
        }

        // ================= seat (stage 1) and holding pocket (stage 2) =================
        void UpdateSeat(double dt)
        {
            if (Seat == null) { seatDwell = 0; SeatValid = false; SeatColor = 0; return; }
            if (ColorLightOn && !SeatValid)
            {
                seatDwell += dt;
                if (seatDwell >= ColorDwell)
                {
                    SeatValid = true;
                    if (ForceUnknownNext) { ForceUnknownNext = false; SeatColor = 0; UnknownReads++; }
                    else SeatColor = Seat.TrueColor;   // the sensor looks at the ACTUAL ball, never at the pump it came from
                }
            }
        }

        void MovePocket(double dt)
        {
            if (ToPocket == null && Seat != null && Pocket == null && seatGateCmd && SeatGateOpen > 0.7)
            {
                ToPocket = Seat; Seat = null; SeatValid = false; SeatColor = 0; seatDwell = 0;
                ToPocket.Loc = BallLoc.Pocket; ToPocket.P = L.SeatToPocket; ToPocket.S = 0; ToPocket.V = 0;
            }
            if (ToPocket != null)
            {
                var b = ToPocket; Roll(b, b.P.Length, PocketV, dt); b.Pos = b.P.At(b.S);
                if (b.S >= b.P.Length - 0.02) { Pocket = b; ToPocket = null; b.P = null; b.V = 0; b.Pos = new V3(0, 0, L.ZPocket); }
            }
        }

        bool relEdge;
        void ReleasePocket()
        {
            if (Pocket == null || !(pocketCmd && PocketGateOpen > 0.7)) { if (!pocketCmd) relEdge = false; return; }
            if (ChuteIdx >= 0 && ChuteIdx == ChuteStation(chuteCmd) && chuteCmd > 0 && !ArmBusy())
            {
                var b = Pocket; Pocket = null; b.Loc = BallLoc.Return; b.Where = ChuteIdx; b.P = Comb[ChuteIdx]; b.S = 0; b.V = 0; b.Pos = b.P.At(0);
                Return.Add(b);
            }
            else if (!relEdge) { relEdge = true; MissedReleases++; }   // interlock: the arm is not under the pocket, the ball stays held
        }

        void MoveReturn(double dt)
        {
            for (int j = 0; j < Return.Count; j++)
            {
                var b = Return[j]; double Lp = b.P.Length, limit = Lp + 5000;
                for (int i = j - 1; i >= 0; i--) if (Return[i].Where == b.Where) { limit = Return[i].S - Spacing; break; }
                Roll(b, limit, ReturnV, dt);
                if (b.S >= Lp)
                {
                    Return.RemoveAt(j); j--;
                    b.P = null; b.V = 0; b.S = 0;
                    if (b.Where < 6) { b.Loc = BallLoc.Hopper; Hopper[b.Where].Add(b); }
                    else { b.Loc = BallLoc.Reject; b.Where = -1; RejectBin.Add(b); }
                    continue;
                }
                b.Pos = b.P.At(b.S);
            }
        }

        // ================= stepper-driven rotary chute =================
        void UpdateChute(double dt)
        {
            if (chuteCmd <= 0) return;
            int tgt = ChuteStation(chuteCmd); double ta = L.StationAngle[tgt];
            double d = ((ta - ChuteAngle + 540) % 360) - 180;
            if (ChuteIdx == tgt && Math.Abs(d) < 0.01) return;
            if (!motionEn) return;
            if (ArmBusy() || (Pocket != null && PocketGateOpen > 0.05)) { InterlockHits++; return; }   // never move with a ball on the arm or while the pocket is releasing
            ChuteIdx = -1;
            double step = ChuteSpeed * dt;
            if (Math.Abs(d) <= step) { ChuteAngle = ta; ChuteIdx = tgt; } else ChuteAngle += Math.Sign(d) * step;
            ChuteAngle = ((ChuteAngle + 540) % 360) - 180;
        }

        // ================= stored balls ease toward their arranged slots =================
        void UpdateStored(double dt)
        {
            for (int h = 0; h < Layout.N; h++)
            {
                var list = Hopper[h];
                for (int i = 0; i < list.Count; i++) Settle(list[i], L.Slot(h, i), dt);
            }
            for (int i = 0; i < Bulk.Count; i++) Settle(Bulk[i], L.BulkSlot(i), dt);
            for (int i = 0; i < RejectBin.Count; i++) Settle(RejectBin[i], L.RejectSlot(i), dt);
        }

        void Settle(Ball b, V3 target, double dt)
        {
            double d = V3.Dist(b.Pos, target);
            if (d < 1e-6) return;
            double sp = Math.Max(StoredSpeed, d * 7);
            b.Pos = V3.MoveToward(b.Pos, target, sp * dt);
        }

        // ================= test controls and bookkeeping =================
        /// <summary>Put one ball in the wrong hopper (as if an operator mis-stocked it). It is first in line to be dispensed.</summary>
        public string PlantWrongBall()
        {
            for (int tries = 0; tries < 20; tries++)
            {
                int a = rng.Next(Layout.N), b = rng.Next(Layout.N);
                if (a == b || Hopper[a].Count < 3) continue;
                var ball = Hopper[a][Hopper[a].Count - 1]; Hopper[a].RemoveAt(Hopper[a].Count - 1);
                ball.Where = b; ball.Planted = true; Hopper[b].Insert(0, ball);
                ball.Pos = L.Slot(b, 0); ball.Teleport = true;
                return string.Format("Put a {0} ball in the {1} hopper", Colors.Names[ball.TrueColor], Colors.Names[b + 1]);
            }
            return "No hopper had spare balls to plant";
        }

        /// <summary>Operator empties the reject bin into the bulk hopper (to be sorted again).</summary>
        public int ClearRejectBin()
        {
            int n = RejectBin.Count;
            foreach (var b in RejectBin) { b.Loc = BallLoc.Bulk; b.Where = -1; Bulk.Add(b); b.Teleport = true; b.Pos = L.BulkSlot(Bulk.Count - 1) + new V3(0, 0, 60); }
            RejectBin.Clear();
            return n;
        }

        public int OutsideStorage
        {
            get
            {
                int n = Bulk.Count + Feed.Count + Return.Count + (Seat != null ? 1 : 0) + (ToPocket != null ? 1 : 0) + (Pocket != null ? 1 : 0);
                for (int k = 0; k < Layout.N; k++) n += AutoRoll[k].Count + VisRoll[k].Count + (Pumps[k].Out != null ? 1 : 0);
                return n;
            }
        }

        public InventoryStats Stats()
        {
            var s = new InventoryStats();
            var seen = new bool[Balls.Count]; int counted = 0; string prob = "";
            Action<Ball, BallLoc, string> add = (b, loc, where) =>
            {
                if (b == null) return;
                counted++;
                if (b.Id < 0 || b.Id >= seen.Length) { prob = "bad id"; return; }
                if (seen[b.Id]) prob = "duplicate ball " + b.Id + " in " + where;
                seen[b.Id] = true;
                if (b.Loc != loc) prob = "ball " + b.Id + " tagged " + b.Loc + " but held in " + where;
                s.Total[b.TrueColor]++;
            };
            for (int h = 0; h < Layout.N; h++)
            {
                foreach (var b in Hopper[h]) { add(b, BallLoc.Hopper, "hopper"); if (b.TrueColor == h + 1) s.InOwnHopper[b.TrueColor]++; else s.Misfiled[b.TrueColor]++; s.HopperCount[h + 1]++; }
                var p = Pumps[h];
                foreach (var b in p.Column) { add(b, BallLoc.Pump, "pump column"); s.InPump[b.TrueColor]++; }
                s.ColumnCount[h + 1] = p.Column.Count;
                add(p.Cup, BallLoc.Pump, "pump cup"); if (p.Cup != null) s.InPump[p.Cup.TrueColor]++;
                add(p.RampBall, BallLoc.Pump, "pump ramp"); if (p.RampBall != null) s.InPump[p.RampBall.TrueColor]++;
                add(p.Out, BallLoc.Collect, "pump outlet"); if (p.Out != null) s.Circulating[p.Out.TrueColor]++;
                foreach (var b in AutoRoll[h]) { add(b, BallLoc.Collect, "auto collector"); s.Circulating[b.TrueColor]++; }
                foreach (var b in VisRoll[h]) { add(b, BallLoc.Collect, "visitor collector"); s.Circulating[b.TrueColor]++; }
            }
            foreach (var b in Bulk) { add(b, BallLoc.Bulk, "bulk hopper"); s.InBulk[b.TrueColor]++; }
            foreach (var b in Feed) { add(b, BallLoc.Feed, "feeder"); s.Circulating[b.TrueColor]++; }
            if (Seat != null) { add(Seat, BallLoc.Seat, "seat"); s.Circulating[Seat.TrueColor]++; }
            if (ToPocket != null) { add(ToPocket, BallLoc.Pocket, "seat gate"); s.Circulating[ToPocket.TrueColor]++; }
            if (Pocket != null) { add(Pocket, BallLoc.Pocket, "pocket"); s.Circulating[Pocket.TrueColor]++; }
            foreach (var b in Return) { add(b, BallLoc.Return, "chute/return tube"); s.Circulating[b.TrueColor]++; }
            foreach (var b in RejectBin) { add(b, BallLoc.Reject, "reject"); s.InReject[b.TrueColor]++; }
            s.Grand = counted;
            if (counted != Balls.Count && prob == "") prob = "count " + counted + " != " + Balls.Count;
            for (int i = 0; i < seen.Length && prob == ""; i++) if (!seen[i]) prob = "ball " + i + " is in no container";
            s.Problem = prob; s.Consistent = prob == "";
            return s;
        }
    }
}
