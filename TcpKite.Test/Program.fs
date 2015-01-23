
open System
open System.Net
open System.Net.Sockets

open TcpKite

type TestPM() = 
    interface Proxy.IPortManager with
        member x.Get () = 16001
        member x.Return _ = ()

let listenEP = new IPEndPoint (IPAddress.Any, 15001)
let pm = TestPM ()

Proxy.createServer listenEP pm
