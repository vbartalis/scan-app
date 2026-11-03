namespace ScanApp.Core.Combine;

public static class NelderMead
{
    /// <summary>
    /// Minimises <paramref name="f"/> with the downhill simplex, starting from <paramref name="start"/> and a simplex
    /// spread by <paramref name="step"/> per coordinate (a zero step holds that coordinate). Stops when the simplex's
    /// values agree within <paramref name="tolerance"/> or after <paramref name="maxEvaluations"/> calls of f.
    /// </summary>
    public static (double[] X, double F) Minimize(Func<double[], double> f, double[] start, double[] step,
                                                  int maxEvaluations, double tolerance = 1e-12)
    {
        int n = start.Length;
        var points = new double[n + 1][];
        var values = new double[n + 1];
        for (int i = 0; i <= n; i++)
        {
            points[i] = (double[])start.Clone();
            if (i > 0) points[i][i - 1] += step[i - 1];
            values[i] = f(points[i]);
        }
        int evaluations = n + 1;
        var centre = new double[n];

        while (evaluations < maxEvaluations)
        {
            Array.Sort(values, points);
            if (Math.Abs(values[n] - values[0]) <= tolerance) break;

            Array.Clear(centre);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) centre[j] += points[i][j] / n;
            double[] Along(double a)
            {
                var p = new double[n];
                for (int j = 0; j < n; j++) p[j] = centre[j] + a * (points[n][j] - centre[j]);
                return p;
            }

            var reflected = Along(-1);
            double fr = f(reflected);
            evaluations++;
            if (fr < values[0])
            {
                var expanded = Along(-2);
                double fe = f(expanded);
                evaluations++;
                (points[n], values[n]) = fe < fr ? (expanded, fe) : (reflected, fr);
            }
            else if (fr < values[n - 1])
            {
                (points[n], values[n]) = (reflected, fr);
            }
            else
            {
                var contracted = Along(fr < values[n] ? -0.5 : 0.5);
                double fc = f(contracted);
                evaluations++;
                if (fc < Math.Min(fr, values[n]))
                {
                    (points[n], values[n]) = (contracted, fc);
                }
                else
                {
                    for (int i = 1; i <= n; i++)
                    {
                        for (int j = 0; j < n; j++) points[i][j] = points[0][j] + 0.5 * (points[i][j] - points[0][j]);
                        values[i] = f(points[i]);
                        evaluations++;
                    }
                }
            }
        }

        int best = 0;
        for (int i = 1; i <= n; i++)
            if (values[i] < values[best]) best = i;
        return (points[best], values[best]);
    }
}
