// 与 multi/GlobalUsings.cs 同样的原因：本项目同时启用 WPF 与 WinForms，
// 隐式 using 会让 Application / Size / Color / Brush 等与 System.Drawing 冲突。
global using System;
global using System.Collections.Generic;
global using System.IO;

global using Application = System.Windows.Application;
global using Size = System.Windows.Size;
global using Color = System.Windows.Media.Color;
global using Brush = System.Windows.Media.Brush;
global using Point = System.Windows.Point;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using VerticalAlignment = System.Windows.VerticalAlignment;
global using MessageBox = System.Windows.MessageBox;
