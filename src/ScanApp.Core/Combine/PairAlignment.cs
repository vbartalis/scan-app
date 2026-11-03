namespace ScanApp.Core.Combine;

/// <summary>
/// The pose and constant speeds that put a moving pass onto a reference pass. A map cell (column c, row r) of a pass
/// is the point (x, s·t) of that pass's own frame, with x = XMinMm + (c + 0.5)·ColumnMm the scanner X in mm,
/// t = (r + 0.5)·RowS its moving time and s its speed. A moving-frame point p lies at
/// p_ref = R(ThetaRad)·p + (Tx, Ty) in the reference frame, whose points are (x, ReferenceSpeed·t).
/// <see cref="Score"/> is <see cref="PairAligner.Score"/> at this pose; <see cref="RunnerUpRatio"/> is the best score
/// of a clearly different pose divided by it, and <see cref="RunnerUp"/> that pose. How certain the alignment is, is
/// judged on features (<see cref="Certainty"/>), not on this ratio.
/// </summary>
public sealed record PairAlignment(double ThetaRad, double ReferenceSpeed, double MovingSpeed, double Tx, double Ty,
                                   double Score, double RunnerUpRatio)
{
    /// <summary>The refined best clearly different rival (its own <see cref="Score"/> set); null when none was refined.</summary>
    public PairAlignment? RunnerUp { get; init; }

    /// <summary>Maps a moving-frame point (x, y = MovingSpeed·t) into the reference frame.</summary>
    public (double X, double Y) ToReference(double x, double y)
    {
        double c = Math.Cos(ThetaRad), s = Math.Sin(ThetaRad);
        return (c * x - s * y + Tx, s * x + c * y + Ty);
    }

    /// <summary>
    /// The same alignment seen from the other side: it maps reference-frame points into the moving frame. The runner-up
    /// is not carried over.
    /// </summary>
    public PairAlignment Inverse()
    {
        double c = Math.Cos(ThetaRad), s = Math.Sin(ThetaRad);
        return new PairAlignment(-ThetaRad, MovingSpeed, ReferenceSpeed, -(c * Tx + s * Ty), -(-s * Tx + c * Ty),
            Score, RunnerUpRatio);
    }
}
