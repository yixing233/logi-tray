// 多品牌版同时启用 WPF 与 WinForms（WPF 负责亚克力界面，WinForms 负责托盘图标），
// 而 ImplicitUsings 会引入 System.Windows.Forms 与 System.Drawing 的全局 using，
// 导致 Button / Application / Color / Brush 等类型与 WPF 同名冲突。
//
// 这里用别名把歧义消掉，与完整版 wpf/GlobalUsings.cs 保持一致的做法。
// 需要 GDI 类型的文件（托盘图标绘制）请显式写 Gdi* 别名。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading.Tasks;
global using System.Windows;
global using System.Windows.Controls;
global using System.Windows.Controls.Primitives;
global using System.Windows.Data;
global using System.Windows.Input;
global using System.Windows.Media;

global using Color = System.Windows.Media.Color;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Application = System.Windows.Application;
global using Cursors = System.Windows.Input.Cursors;
global using Orientation = System.Windows.Controls.Orientation;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using VerticalAlignment = System.Windows.VerticalAlignment;
global using Button = System.Windows.Controls.Button;
global using CheckBox = System.Windows.Controls.CheckBox;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using MessageBox = System.Windows.MessageBox;
global using Image = System.Windows.Controls.Image;
