namespace WBall.Model;

/// <summary>场景快照 DTO；文件读写由 Application 层的 SceneStore 负责。</summary>
public sealed class SceneSnapshot
{
    public int Format { get; set; } = 1;
    public string App { get; set; } = "WBall";
    public double GravityG { get; set; } = 10;
    public bool BallCollision { get; set; } = true;
    public int Seed { get; set; } = 42;
    public double WorldWidth { get; set; } = SceneWorld.DefaultWorldWidth;
    public double WorldHeight { get; set; } = SceneWorld.DefaultWorldHeight;
    public List<SceneObjectDto> Objects { get; set; } = [];
    public List<WireframeDto> Wireframes { get; set; } = [];
    public List<SolidDto> Solids { get; set; } = [];
    public List<BallDto> Balls { get; set; } = [];
}

public sealed class WireframeDto
{
    public string Id { get; set; } = "";
    public bool Closed { get; set; } = true;
    public List<WirePointDto> Points { get; set; } = [];
}

public sealed class WirePointDto
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class SolidDto
{
    public string Id { get; set; } = "";
    public string Color { get; set; } = MeshSolid.DefaultColor;
    public List<WirePointDto> Points { get; set; } = [];
}

public sealed class SceneObjectDto
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "block";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 40;
    public double H { get; set; } = 40;
    public double DirX { get; set; }
    public double DirY { get; set; } = 1;
    public double InfluenceRadius { get; set; } = 160;
    public double StrengthG { get; set; } = 10;
    public double Rotation { get; set; }
    public string? PatchJson { get; set; }
    public string? Name { get; set; }
}

public sealed class BallDto
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public string Color { get; set; } = "#3B82F6";
    public double Weight { get; set; } = 1;
    public double Size { get; set; } = 12;
    public long Multiplier { get; set; } = 1;
    public Dictionary<string, string>? Props { get; set; }
}
