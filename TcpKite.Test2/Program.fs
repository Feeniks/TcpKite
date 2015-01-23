
open System
open System.Net
open System.Net.Sockets

open TcpKite

let serverEP = new IPEndPoint (IPAddress.Parse "127.0.0.1", 15001)
let localEP = new IPEndPoint (IPAddress.Parse "127.0.0.1", 80)

let ts = TimeSpan.FromMilliseconds 100000.0

let port,handle = Proxy.createClient serverEP localEP ts

printfn "PORT: %A" port

let rec sleep () = 
    printfn "tick"
    System.Threading.Thread.Sleep 2000
    sleep ()

sleep ()