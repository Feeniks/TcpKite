namespace TcpKite

open System
open System.Net.Sockets

open TcpKite
open TcpKite.Protocol

module Client = 

    type private SendCmd = SC_Send of byte array | SC_Exit

    type private RecvCmd = RC_Recv | RC_CheckTimeout | RC_Exit

    type private SendAgent = SendCmd MailboxProcessor

    type private RecvAgent = RecvCmd MailboxProcessor

    type ClientHandle = internal {
        sender:SendAgent;
        receiver:RecvAgent;
    }

    type IProxy = 
        abstract member Send : byte array -> unit
        abstract member Kill : unit -> unit

    let rec private sendData (s:Socket) (buf:byte array) = 
        match Array.length buf with
        | 0 -> ()
        | _ ->
            let sent = s.Send buf
            Array.sub buf sent (buf.Length - sent) |> sendData s

    let rec private send (sock:Socket) (a:SendAgent) = async {
        let! msg = a.Receive ()
        match msg with
        | SC_Send d ->
            sendData sock d
            return! send sock a

        | SC_Exit -> try sock.Close () with | _ -> ()
    }

    let private read (s:Socket) = 
        match s.Available with
        | x when x > 0 -> 
            let buf = Array.init s.Available (fun _ -> 0uy)
            let recvd = s.Receive buf
            match recvd with | 0 -> None | _ -> buf.[..(recvd - 1)] |> Some
        | _ -> None

    let rec private recv (sock:Socket) (proxy:IProxy) (timeout:TimeSpan) (updated:DateTime) (sender:SendAgent) (a:RecvAgent) = async {
        let! msg = a.Receive ()
        match msg with
        | RC_Recv ->
            let updated' = 
                match read sock with 
                | None ->
                    async {
                        do! Async.Sleep 20
                        a.Post RC_Recv
                    } |> Async.Start
                    updated
                | Some b ->
                    proxy.Send b
                    a.Post RC_Recv
                    DateTime.Now

            return! recv sock proxy timeout updated' sender a

        | RC_CheckTimeout ->
            let idle = DateTime.Now - updated
            match idle > timeout with 
            | false -> 
                async {
                    do! Async.Sleep 1000
                    a.Post RC_CheckTimeout
                } |> Async.Start

                return! recv sock proxy timeout updated sender a
            | true -> 
                sender.Post SC_Exit
                try sock.Close () with | _ -> ()
                proxy.Kill ()

        | RC_Exit -> try sock.Close () with | _ -> ()
    }

    ///Public interface

    let create (sock:Socket) (timeout:TimeSpan) (proxy:IProxy) = 
        let sender = send sock |> SendAgent.Start
        let receiver = recv sock proxy timeout DateTime.Now sender |> RecvAgent.Start
        receiver.Post RC_Recv
        receiver.Post RC_CheckTimeout
        {
            ClientHandle.sender = sender;
            ClientHandle.receiver = receiver;
        }

    let destroy (handle:ClientHandle) = 
        handle.sender.Post SC_Exit
        handle.receiver.Post RC_Exit

    let write (handle:ClientHandle) (data:byte array) = SC_Send data |> handle.sender.Post