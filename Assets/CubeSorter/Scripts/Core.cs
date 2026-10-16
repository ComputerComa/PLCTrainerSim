using System;
using System.Collections.Generic;

namespace CubeSorter
{
    /// <summary>Plain double vector (Blender-frame millimetres: X along the gantry, Y away from the visitor, Z up). No UnityEngine dependency.</summary>
    public struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static V3 operator *(V3 a, double k) { return new V3(a.X * k, a.Y * k, a.Z * k); }
        public double Len { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
        public static double Dist(V3 a, V3 b) { return (a - b).Len; }
        public static V3 Lerp(V3 a, V3 b, double t) { return a + (b - a) * t; }
        public V3 Norm { get { double l = Len; return l < 1e-9 ? new V3(0, 0, 1) : this * (1.0 / l); } }
        public static V3 MoveToward(V3 a, V3 b, double maxStep)
        {
            V3 d = b - a; double l = d.Len;
            if (l <= maxStep || l < 1e-9) return b;
            return a + d * (maxStep / l);
        }
        public override string ToString() { return string.Format("({0:F1},{1:F1},{2:F1})", X, Y, Z); }
    }

    /// <summary>Polyline with arc-length parameter s (mm).</summary>
    public class Path
    {
        public readonly V3[] P;
        public readonly double[] Cum;
        public double Length { get { return Cum[Cum.Length - 1]; } }
        public Path(params V3[] pts)
        {
            P = pts; Cum = new double[pts.Length];
            for (int i = 1; i < pts.Length; i++) Cum[i] = Cum[i - 1] + V3.Dist(pts[i - 1], pts[i]);
        }
        int Seg(double s)
        {
            for (int i = 1; i < Cum.Length; i++) if (s <= Cum[i]) return i;
            return Cum.Length - 1;
        }
        public V3 At(double s)
        {
            if (s <= 0) return P[0];
            if (s >= Length) return P[P.Length - 1];
            int i = Seg(s);
            double seg = Cum[i] - Cum[i - 1];
            return V3.Lerp(P[i - 1], P[i], seg < 1e-9 ? 0 : (s - Cum[i - 1]) / seg);
        }
        public V3 DirAt(double s)
        {
            int i = Seg(Math.Max(0, Math.Min(Length, s)));
            return (P[i] - P[i - 1]).Norm;
        }
    }

    /// <summary>Where a ball is. Every ball is in exactly one of these containers at any time.</summary>
    public enum BallLoc { Hopper, Pump, Collect, Bulk, Feed, Seat, Pocket, Return, Reject }

    public class Ball
    {
        public int Id;
        public int TrueColor;        // 1..6 (the real color of the ball; the color SENSOR reads this, the dispenser color is separate)
        public BallLoc Loc;
        public int Where = -1;       // hopper / pump / track index 0..5 when applicable
        public V3 Pos;               // world position (Blender frame mm), owned by the sim
        public Path P;               // current rolling path (null = not on a path)
        public double S, V;          // arc length and speed on P
        public bool Teleport;        // view should snap to Pos instead of easing (set when the ledger moves a ball by hand)
        public int ReqId;            // request that released this ball from a pump (visual tracking only; the PLC does NOT read this)
        public bool Planted;         // test control: this ball was put in the wrong hopper by hand
    }

    public static class Colors
    {
        // index 0 = none/unknown, 1..6 = the six ball colors in left-to-right hopper order
        public static readonly string[] Names = { "Unknown", "Red", "Orange", "Blue", "Green", "White", "Black" };
    }
}
