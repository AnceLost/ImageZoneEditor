using System.Text.Json.Serialization;
using System;

namespace ImageZoneEditor.Models;

public class Zone
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Новая зона";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Angle { get; set; } // Угол поворота в градусах
}