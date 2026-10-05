using System;
using System.IO;
using System.Text;
using GameFramework.Config.EditorTools;

namespace GameFramework.Config.Tool
{
    /// <summary>
    /// 独立的配置表导出器：不打开 Unity 也能导出 / 校验配置表。
    ///
    ///   ConfigTool.exe                     自动往上找工程根（含 Assets\Config\Excel）
    ///   ConfigTool.exe D:\path\to\project  显式指定工程根
    ///   ConfigTool.exe --check             只校验 + 报数据大小，一个文件都不写
    ///   ConfigTool.exe --no-pause          跑完不等按键（批处理 / CI 用）
    ///
    /// 产出和 Unity 菜单「Tools/配置表/导出全部表」完全一样（同一份导出代码）。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch (Exception) { /* 某些终端不支持，忽略 */ }

            bool check = false;
            bool noPause = false;
            string root = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg == "--check" || arg == "--dry-run") check = true;
                else if (arg == "--no-pause") noPause = true;
                else if (arg == "-h" || arg == "--help") { PrintHelp(); return 0; }
                else if (arg.Length > 0 && arg[0] != '-') root = arg;
                else { Console.Error.WriteLine("不认识的参数：" + arg); PrintHelp(); return 2; }
            }

            if (root == null) root = FindProjectRoot();
            if (root == null)
            {
                Console.Error.WriteLine("找不到工程根。");
                Console.Error.WriteLine("没在参数里给，也没能从 exe 所在目录 / 当前目录往上找到 Assets\\Config\\Excel。");
                PrintHelp();
                Pause(noPause);
                return 2;
            }

            root = Path.GetFullPath(root);
            Console.WriteLine((check ? "[校验] " : "[导出] ") + "工程根：" + root + Environment.NewLine);

            ConfigExportReport report;
            try
            {
                report = ConfigExporter.ExportAll(root, check);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("导出过程异常：");
                Console.Error.WriteLine(ex.ToString());
                Pause(noPause);
                return 1;
            }

            Console.WriteLine(report.ToText(check));
            if (!report.Success) Console.WriteLine("存在错误，本次没有写任何文件。");

            Pause(noPause);
            return report.Success ? 0 : 1;
        }

        /// <summary>从 exe 所在目录、当前目录各自往上找带 Assets/Config/Excel 的那一层。</summary>
        private static string FindProjectRoot()
        {
            string[] starts = { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };

            for (int i = 0; i < starts.Length; i++)
            {
                if (string.IsNullOrEmpty(starts[i])) continue;

                DirectoryInfo dir = new DirectoryInfo(starts[i]);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "Assets", "Config", "Excel")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }

            return null;
        }

        private static void Pause(bool noPause)
        {
            if (noPause || !Environment.UserInteractive) return;
            Console.WriteLine();
            Console.Write("按回车键退出…");
            Console.ReadLine();
        }

        private static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("用法：ConfigTool.exe [工程根目录] [--check] [--no-pause]");
            Console.WriteLine("  工程根目录   不填就自动往上找（含 Assets\\Config\\Excel 的那一层）");
            Console.WriteLine("  --check      只校验并报数据大小，不写任何文件");
            Console.WriteLine("  --no-pause   跑完不等按键（批处理用）");
        }
    }
}