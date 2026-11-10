namespace FO3SessionTool;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Config cfg;
        try { cfg = Config.Load(); }
        catch (Exception ex)
        {
            MessageBox.Show("Không đọc được config.json:\n" + ex.Message, "FO3 Session Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new MainForm(cfg));
    }
}
