using ConPhone.Core;

int passed=0;
void Check(string name,bool ok) { if(!ok) throw new Exception("FAIL: "+name); passed++; Console.WriteLine("PASS: "+name); }
var keys=new KeyboardState();
Check("ASCII mapping",Hid.Usage(0x41)==4 && Hid.Usage(0x5A)==29);
Check("right modifiers",Hid.Usage(0x11,extended:true)==0xE4 && Hid.Usage(0x10,0x36)==0xE5);
Check("Korean virtual key mapping",Hid.Usage(0x15)==0x90 && Hid.Usage(0x19)==0x91);
Check("key down",keys.Update(4,true)![2]==4); Check("repeat suppressed",keys.Update(4,true) is null);
Check("key up",keys.Update(4,false)!.All(x=>x==0));
for(byte i=4;i<11;i++) keys.Update(i,true);
Check("rollover explicit",keys.Snapshot().Skip(2).All(x=>x==1)); keys.Clear();
var pointer=Hid.Encode(new PointerPacket(1,300,-300,260)).ToArray();
short Signed(byte l,byte h)=>unchecked((short)(l|(h<<8)));
Check("mouse six bytes",pointer.All(p=>p.Data.Length==6));
Check("mouse signed displacement",pointer.Sum(p=>(int)Signed(p.Data[1],p.Data[2]))==300 && pointer.Sum(p=>(int)Signed(p.Data[3],p.Data[4]))==-300);
Check("wheel conserved",pointer.Sum(p=>(int)unchecked((sbyte)p.Data[5]))==260);
Check("button preserved",pointer.All(p=>p.Data[0]==1));
var scaler=new PointerScaler(); scaler.Configure(25,50,true);
Check("fractional small motion",Enumerable.Range(0,100).Sum(_=>scaler.Apply(new PointerPacket(0,1)).X)==25);
Check("fractional scroll direction",Enumerable.Range(0,4).Sum(_=>scaler.Apply(new PointerPacket(0,Wheel:1)).Wheel)==-2);
scaler.Reset(); Check("reset carry",scaler.Apply(new PointerPacket(0,1)).X==0);
var box=new Outbox(2); box.TryAdd(new(1,new PointerPacket(0,3))); box.TryAdd(new(1,new PointerPacket(0,2)));
Check("movement coalescing",box.Count==1 && (await box.TakeAsync(default)).Packet==new PointerPacket(0,5));
box.TryAdd(new(1,new PointerPacket(1))); box.TryAdd(new(1,new PointerPacket(0)));
Check("button ordering barrier",box.Count==2 && !box.TryAdd(new(1,new KeyboardPacket(new byte[8])))); box.Clear();
Check("queue clear",box.Count==0);
using(var timeout=new CancellationTokenSource(20)) { bool cancelled=false; try { await box.TakeAsync(timeout.Token); } catch(OperationCanceledException) { cancelled=true; } Check("queue cancellation",cancelled); }
// Descriptor lengths are checked independently, not by checking encoder constants against themselves.
int id=0,size=0,count=0,depth=0; Dictionary<int,int> bits=[];
var map=Hid.ReportMap;
for(int i=0;i<map.Length;)
{
    int prefix=map[i++],n=(prefix&3)==3?4:prefix&3,value=0;
    for(int j=0;j<n;j++) value|=map[i++]<<(j*8);
    switch(prefix&0xFC) { case 0x84:id=value;break; case 0x74:size=value;break; case 0x94:count=value;break; case 0x80:bits[id]=bits.GetValueOrDefault(id)+size*count;break; case 0xA0:depth++;break;case 0xC0:depth--;break; }
    if(depth<0) throw new Exception("Descriptor nesting");
}
Check("HID descriptor 8/6 bytes",bits[1]==64 && bits[2]==48 && depth==0);
var r=new KeyboardRouter();
var combo=new KeyChord(0x77,ChordModifiers.Control|ChordModifiers.Shift);
r.Configure(combo,new(0x78),new(0x4C,ChordModifiers.Control|ChordModifiers.Alt),true);
r.Process(0xA2,false);r.Process(0xA0,false);
Check("combo first press enters",r.Process(0x77,false).Command==InputCommand.EnterPhone);
Check("combo repeat never leaks",r.Process(0x77,false).Suppress && r.Process(0x77,false).Command==InputCommand.None);
Check("held chord cannot activate",!r.TryEnterPhone());
Check("trigger up swallowed",r.Process(0x77,true).Suppress);
Check("local modifiers get their up",!r.Process(0xA0,true).Suppress && !r.Process(0xA2,true).Suppress);
Check("activate after all release",r.TryEnterPhone());
r.Process(0xA2,false);
Check("emergency requires exact modifiers",r.Process(0x78,false).Command==InputCommand.None);
r.Process(0x78,true);r.Process(0xA2,true);
Check("emergency immediately stops router",r.Process(0x78,false).Command==InputCommand.ReturnLocal && !r.Remote);
r.Process(0x78,true);
Check("can activate again",r.TryEnterPhone());
r.Process(0xA2,false);r.Process(0xA4,false);
Check("explicit lock command",r.Process(0x4C,false).Command==InputCommand.LockAndReturn && !r.Remote);
Check("phone modifier ups do not leak locally",r.Process(0xA4,true).Suppress && r.Process(0xA2,true).Suppress);
r.Process(0x4C,true);
r.SetRecording(true);r.Process(0xA2,false);r.Process(0xA0,false);
var recorded=r.Process(0x77,false);
Check("recording wins over active shortcut",recorded.Captured==combo && recorded.Command==InputCommand.None && recorded.Suppress);
Check("recording repeat suppressed",r.Process(0x77,false).Captured is null && r.Process(0x77,false).Suppress);
r.Process(0x77,true);r.Process(0xA0,true);r.Process(0xA2,true);
Check("escape cancels recording",r.Process(0x1B,false).Command==InputCommand.CancelRecording);r.Process(0x1B,true);r.SetRecording(false);
bool reserved=false;try { KeyboardRouter.Validate(new(0x4C,ChordModifiers.Windows)); } catch(ArgumentException) { reserved=true; } Check("reserved PC lock rejected",reserved);
var stress=new KeyboardRouter();bool cycles=true;
for(int i=0;i<5000;i++)
{
    cycles &= stress.Process(0x77,false).Command==InputCommand.EnterPhone;
    stress.Process(0x77,true);cycles &= stress.TryEnterPhone();
    stress.Process(0x41,false);stress.Process(0x41,true);
    cycles &= stress.Process(0x77,false).Command==InputCommand.ReturnLocal && !stress.Remote;
    cycles &= stress.Process(0x77,false).Suppress;
    stress.Process(0x77,true);
}
Check("5000 enter/return cycles with repeat",cycles && stress.Released);
// A cancelled managed wait must not overlap the next native write.
var serial=new SerialWrites();var native=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int launched=0;bool timedOut=false;
using(var timeout=new CancellationTokenSource(25))
{
    try { await serial.RunAsync(()=>true,()=>{Interlocked.Increment(ref launched);return native.Task;},timeout.Token); }
    catch(OperationCanceledException) { timedOut=true; }
}
var second=serial.RunAsync(()=>true,()=>{Interlocked.Increment(ref launched);return Task.CompletedTask;},default);
await Task.Delay(30);Check("cancelled wait retains native operation",timedOut && launched==1 && !second.IsCompleted);
native.SetResult();await second;Check("next native starts after completion",launched==2);
bool obsolete=await serial.RunAsync(()=>false,()=>throw new Exception("must not run"),default);Check("stale epoch not sent",!obsolete);
int concurrent=0,max=0;
await Task.WhenAll(Enumerable.Range(0,100).Select(_=>serial.RunAsync(()=>true,async()=> { int n=Interlocked.Increment(ref concurrent);max=Math.Max(max,n);await Task.Delay(1);Interlocked.Decrement(ref concurrent); },default)));
Check("100 concurrent callers serialize",max==1);
Console.WriteLine($"{passed} core checks passed. No physical Bluetooth/phone tests are represented by this result.");
