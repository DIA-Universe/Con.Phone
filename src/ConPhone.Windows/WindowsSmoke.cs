using System.Text;
using System.Text.Json;
using ConPhone.Core;
namespace ConPhone.Windows;

// Executed on a Windows CI desktop against real WinForms controls, native OLE clipboard and DPAPI.
// Keyboard events are injected at our routing boundary, NOT presented as physical-device testing.
internal static class WindowsSmoke
{
    private static readonly List<string> Results=[];
    private static void Check(string name,bool ok) { Results.Add((ok?"PASS ":"FAIL ")+name); if(!ok) throw new InvalidOperationException(name); }
    private static async Task Wait(Func<bool> predicate,int milliseconds=5000)
    {
        var start=System.Diagnostics.Stopwatch.StartNew();
        while(!predicate()) { if(start.ElapsedMilliseconds>milliseconds) throw new TimeoutException("Smoke wait exceeded: "+milliseconds); await Task.Delay(40); }
    }
    public static int Run(string output,bool quick)
    {
        Directory.CreateDirectory(output); Results.Clear();
        Environment.SetEnvironmentVariable("CONPHONE_TEST_DATA",Path.Combine(output,"isolated-app-data"));
        int code=1;
        using var form=new MainForm(true);
        form.Shown+=async(_,_)=>
        {
            try
            {
                await form.TestInput.Started; await form.TestClipboard.Started; await Task.Delay(250);
                form.ShowPage("input"); form.TestEditor.PerformClick();
                int actions=0; form.TestInput.Command+=_=>actions++;
                form.TestInput.TestKey(0xA2,false); form.TestInput.TestKey(0xA0,false); form.TestInput.TestKey(0x77,false);
                form.TestInput.TestKey(0x77,false);form.TestInput.TestKey(0x77,true);form.TestInput.TestKey(0xA0,true);form.TestInput.TestKey(0xA2,true);
                await Task.Delay(160);
                Check("hotkey editor captures Ctrl+Shift+F8",form.TestEditor.Binding.Chord==new KeyChord(0x77,ChordModifiers.Control|ChordModifiers.Shift));
                Check("recording does not activate global shortcut",actions==0);
                form.TestEditor.FinishRecording(); await form.TestSaveSettingsAsync();
                Check("shortcut persisted and reloaded",SettingsStore.Load().ToggleHotkey.Chord==form.TestEditor.Binding.Chord);
                var clip=form.TestClipboard;
                Clipboard.SetText("ConPhone native clipboard · 한글 / line 1\nline 2");
                await Wait(()=>clip.Entries.Any(e=>e.Text?.StartsWith("ConPhone native") == true));
                var text=clip.Entries.First(e=>e.Text?.StartsWith("ConPhone native") == true);
                Check("native clipboard text automatically captured",text.Text!.Contains("한글"));
                Clipboard.SetText("replacement text");await Wait(()=>clip.Entries.Any(e=>e.Text=="replacement text"));
                Check("restore existing text item",await clip.RestoreAsync(text.Id));
                Check("native clipboard text content restored",Clipboard.GetText()==text.Text);
                using(var image=new Bitmap(40,24))
                {
                    using(var g=Graphics.FromImage(image)) g.Clear(Color.FromArgb(17,113,211));
                    Clipboard.SetImage(image);
                }
                await Wait(()=>clip.Entries.Any(e=>e.Kind==ClipboardEntryKind.Image));
                var picture=clip.Entries.First(e=>e.Kind==ClipboardEntryKind.Image);
                Clipboard.SetText("overwrite image");await Wait(()=>clip.Entries.Any(e=>e.Text=="overwrite image"));
                Check("restore existing image item",await clip.RestoreAsync(picture.Id));
                using(var restored=Clipboard.GetImage())
                {
                    Check("native image dimensions restored",restored is not null && restored.Width==40 && restored.Height==24);
                    using var pixel=new Bitmap(restored!);Check("native image pixels preserved",pixel.GetPixel(3,3).ToArgb()==Color.FromArgb(17,113,211).ToArgb());
                }
                int before=clip.Entries.Count;await Task.Delay(350);Check("own clipboard writes do not create loops",before==clip.Entries.Count);
                var settings=SettingsStore.Load();settings.ClipboardHistoryLimit=10;settings.PersistClipboardHistory=true;
                await clip.ConfigureAsync(settings);
                for(int i=0;i<14;i++) { string sample=$"smoke sequence {i}";Clipboard.SetText(sample);await Wait(()=>clip.Entries.Any(e=>e.Text==sample)); }
                Check("history bounded at configured count",clip.Entries.Count==10);
                var encrypted=Path.Combine(SettingsStore.Root,"clipboard-history.dpapi");
                await Wait(()=>File.Exists(encrypted));
                var encryptedBytes=File.ReadAllBytes(encrypted);
                Check("history not stored as plaintext",!Encoding.UTF8.GetString(encryptedBytes).Contains("smoke sequence"));
                Check("current-user DPAPI round trip",Encoding.UTF8.GetString(LocalVault.Unseal(encryptedBytes)).Contains("smoke sequence"));
                using(var reloaded=new ClipboardHistoryService(settings)) { await reloaded.Started; Check("encrypted history reload",reloaded.Entries.Count==10); }
                settings.PersistClipboardHistory=false;await clip.ConfigureAsync(settings);
                Check("turning off persistence removes encrypted history",!File.Exists(encrypted));
                await clip.RestoreAsync(clip.Entries[0].Id);
                var privateData=new DataObject();privateData.SetText("DO NOT RECORD THIS TEST",TextDataFormat.UnicodeText);
                privateData.SetData("ExcludeClipboardContentFromMonitorProcessing",new MemoryStream([1,0,0,0]));
                Clipboard.SetDataObject(privateData,true);await Task.Delay(400);
                Check("sensitive clipboard marker respected",!clip.Entries.Any(e=>e.Text=="DO NOT RECORD THIS TEST"));
                await clip.ClearAsync();Check("clear history",clip.Entries.Count==0);
                Clipboard.SetText("Con.Phone · Windows 테스트 항목\n클립보드 복사 및 복원 확인");await Wait(()=>clip.Entries.Count==1);
                if(!quick)
                {
                    foreach(var viewport in new[]{new Size(1060,760),new Size(880,660)})
                    {
                        form.ClientSize=viewport;
                        foreach(string page in new[]{"connection","input","clipboard","diagnostics"})
                        {
                            form.ShowPage(page);ScrollPages(form,false);await Task.Delay(160);form.PerformLayout();
                            Capture(form,Path.Combine(output,$"{page}-{viewport.Width}.png"));
                            Check($"no horizontal layout overflow {page}/{viewport.Width}",NoHorizontalOverflow(form));
                            ScrollPages(form,true);await Task.Delay(100);
                            Capture(form,Path.Combine(output,$"{page}-{viewport.Width}-bottom.png"));
                            Check($"scrolled controls fit {page}/{viewport.Width}",NoHorizontalOverflow(form));
                            ScrollPages(form,false);
                        }
                    }
                    foreach(float scale in new[]{1.5f,2f})
                    {
                        using var enlarged=new MainForm(true,scale); enlarged.Show();
                        await enlarged.TestInput.Started; await enlarged.TestClipboard.Started;await Task.Delay(200);
                        enlarged.ShowPage("input");await Task.Delay(200);
                        Capture(enlarged,Path.Combine(output,$"input-logical-scale-{scale}.png"));
                        Check($"scaled layout overflow {scale}",NoHorizontalOverflow(enlarged));
                        ScrollPages(enlarged,true);await Task.Delay(100);
                        Capture(enlarged,Path.Combine(output,$"input-logical-scale-{scale}-bottom.png"));
                        Check($"scaled scrolled layout {scale}",NoHorizontalOverflow(enlarged));
                        enlarged.TestExit();await Wait(()=>enlarged.IsDisposed);
                    }
                }
                Check("application still responsive after clipboard tests",!form.IsDisposed);
                code=0;
            }
            catch(Exception ex)
            {
                Results.Add("ERROR "+ex);
                try { Capture(form,Path.Combine(output,"failure.png")); } catch { }
            }
            finally
            {
                Results.Add("Scope: real Windows controls + clipboard + DPAPI; synthetic routing; logical scaling is not a physical multi-DPI device test. No physical phone/Bluetooth/lock validation.");
                File.WriteAllText(Path.Combine(output,"windows-validation.txt"),string.Join("\n",Results));
                File.WriteAllText(Path.Combine(output,"windows-validation.json"),JsonSerializer.Serialize(new { Passed=code==0,Results },new JsonSerializerOptions { WriteIndented=true }));
                try { Clipboard.Clear(); } catch { }
                form.TestExit();
            }
        };
        Application.Run(form);return code;
    }
    private static void ScrollPages(Control parent,bool bottom)
    {
        foreach(Control child in parent.Controls)
        {
            if(!child.Visible) continue;
            if(child is Panel panel && child is not TableLayoutPanel && panel.AutoScroll)
                panel.AutoScrollPosition=bottom ? new Point(0,panel.DisplayRectangle.Height) : Point.Empty;
            ScrollPages(child,bottom);
        }
    }
    private static void Capture(Form form,string path)
    {
        using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
    }
    private static bool NoHorizontalOverflow(Control parent)
    {
        foreach(Control child in parent.Controls)
        {
            if(!child.Visible) continue;
            if(child.Left < -3 || child.Right > parent.ClientSize.Width+3)
            { Results.Add($"OVERFLOW {child.GetType().Name} '{child.Text[..Math.Min(32,child.Text.Length)]}' {child.Bounds} parent {parent.ClientSize}");return false; }
            if(!NoHorizontalOverflow(child)) return false;
        }
        return true;
    }
}
