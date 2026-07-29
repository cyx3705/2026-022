namespace WBall.Editing;

public sealed record EditResult(bool Success, string Message)
{
    public static EditResult Ok(string message) => new(true, message);

    public static EditResult Fail(string message) => new(false, message);
}
