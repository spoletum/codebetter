namespace CodeBetter.Ingestor.Test;

public class Calculator
{
    public int Add(int a, int b)
    {
        return a + b;
    }

    public int Subtract(int a, int b)
    {
        return a - b;
    }
}

public class MathOperations
{
    private readonly Calculator _calculator;

    public MathOperations()
    {
        _calculator = new Calculator();
    }

    public int PerformAddition(int x, int y)
    {
        return _calculator.Add(x, y);
    }

    public int PerformSubtraction(int x, int y)
    {
        return _calculator.Subtract(x, y);
    }

    public static int StaticAdd(int a, int b)
    {
        return a + b;
    }

    public event EventHandler? CalculationCompleted;

    protected virtual void OnCalculationCompleted()
    {
        CalculationCompleted?.Invoke(this, EventArgs.Empty);
    }
} 