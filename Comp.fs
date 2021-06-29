(* File MicroC/Comp.fs
   A compiler from micro-C, a sublanguage of the C language, to an
   abstract machine.  Direct (forwards) compilation without
   optimization of jumps to jumps, tail-calls etc.
   sestoft@itu.dk * 2009-09-23, 2011-11-10

   A value is an integer; it may represent an integer or a pointer,
   where a pointer is just an address in the store (of a variable or
   pointer or the base address of an array).

   The compile-time environment maps a global variable to a fixed
   store address, and maps a local variable to an offset into the
   current stack frame, relative to its bottom.  The run-time store
   maps a location to an integer.  This freely permits pointer
   arithmetics, as in real C.  A compile-time function environment
   maps a function name to a code label.  In the generated code,
   labels are replaced by absolute code addresses.

   Expressions can have side effects.  A function takes a list of
   typed arguments and may optionally return a result.

   Arrays can be one-dimensional and constant-size only.  For
   simplicity, we represent an array as a variable which holds the
   address of the first array element.  This is consistent with the
   way array-type parameters are handled in C, but not with the way
   array-type variables are handled.  Actually, this was how B (the
   predecessor of C) represented array variables.

   The store behaves as a stack, so all data except global variables
   are stack allocated: variables, function parameters and arrays.
*)

module Comp

open System.IO
open Absyn
open Machine
open Debug
open Backend

(* ------------------------------------------------------------------- *)

(* Simple environment operations *)

type 'data Env = (string * 'data) list

let rec lookup env x =
    match env with
    | [] -> failwith (x + " not found")
    | (y, v) :: yr -> if x = y then v else lookup yr x

(* A global variable has an absolute address, a local one has an offset: *)

type Var =
    | Glovar of int (* absolute address in stack           *)
    | Locvar of int (* address relative to bottom of frame *)

(* The variable environment keeps track of global and local variables, and
   keeps track of next available offset for local variables *)

type VarEnv = (Var * typ) Env * int

(* The function environment maps function name to label and parameter decs *)

type Paramdecs = (typ * string) list

type FunEnv = (label * typ option * Paramdecs) Env

type LabEnv = label list

let isX86Instr = ref false

(* Bind declared variable in env and generate code to allocate it: *)
// kind : Glovar / Locvar
let rec allocateWithMsg (kind: int -> Var) (typ, x) (varEnv: VarEnv) =
    let varEnv, instrs =
        allocate (kind: int -> Var) (typ, x) (varEnv: VarEnv)

    msg
    <| "\nalloc\n"
       + sprintf "%A\n" varEnv
       + sprintf "%A\n" instrs

    (varEnv, instrs)

and allocate (kind: int -> Var) (typ, x) (varEnv: VarEnv) : VarEnv * instr list =

    msg $"allocate called!{(x, typ)}"

    let (env, newloc) = varEnv

    match typ with
    | TypA (TypA _, _) -> raise (Failure "allocate: array of arrays not permitted")
    | TypA (t, Some i) ->
        let newEnv =
            ((x, (kind (newloc + i), typ)) :: env, newloc + i + 1) //数组内容占用 i个位置,数组变量占用1个位置

        let code = [ INCSP i; GETSP; OFFSET(i - 1); SUB ]
        // info (fun () -> printf "new varEnv: %A\n" newEnv)
        (newEnv, code)
    | _ ->
        let newEnv =
            ((x, (kind (newloc), typ)) :: env, newloc + 1)

        let code = [ INCSP 1 ]

        // info (fun () -> printf "new varEnv: %A\n" newEnv) // 调试 显示分配后环境变化
        (newEnv, code)

(* Bind declared parameters in env: *)

let bindParam (env, newloc) (typ, x) : VarEnv =
    ((x, (Locvar newloc, typ)) :: env, newloc + 1)

let bindParams paras ((env, newloc): VarEnv) : VarEnv = List.fold bindParam (env, newloc) paras

(*
    生成 x86 代码，局部地址偏移 *8 ，因为 x86栈上 8个字节表示一个 堆栈的 slot槽位
    栈式虚拟机 无须考虑，每个栈位保存一个变量
*)
let x86patch code =
    if !isX86Instr then
        code @ [ CSTI -8; MUL ] // x86 偏移地址*8
    else
        code
(* ------------------------------------------------------------------- *)

(* Compiling micro-C statements:
   * stmt    is the statement to compile
   * varenv  is the local and global variable environment
   * funEnv  is the global function environment
*)
let rec breaklab labs =
    match labs with
    | lab :: tr -> lab
    | [] -> failwith "no labs"

let rec continuelab labs =
    match labs with
    | lab1 :: lab2 :: tr ->
        printf "lab2:\n%A\n" lab2
        lab2
    | [] -> failwith "no labs"
    | [ _ ] ->
        printf "\nlabs:\n%A\n" labs
        failwith "no enough labs"

let rec cStmt stmt (varEnv: VarEnv) (funEnv: FunEnv) (lablist: LabEnv) : instr list =
    match stmt with
    | If (e, stmt1, stmt2) ->
        let labelse = newLabel ()
        let labend = newLabel ()

        cExpr e varEnv funEnv lablist
        @ [ IFZERO labelse ]
          @ cStmt stmt1 varEnv funEnv lablist
            @ [ GOTO labend ]
              @ [ Label labelse ]
                @ cStmt stmt2 varEnv funEnv lablist
                  @ [ Label labend ]
    | While (e, body) ->
        let labbegin = newLabel ()
        let labtest = newLabel ()
        let labend = newLabel ()
        let lablist = labend :: labtest :: lablist
        printf "\nlabs:\n%A\n" lablist

        [ GOTO labtest; Label labbegin ]
        @ cStmt body varEnv funEnv lablist
          @ [ Label labtest ]
            @ cExpr e varEnv funEnv lablist
              @ [ IFNZRO labbegin; Label labend ]
    | DoWhile (body, e) ->
        let labbegin = newLabel ()
        let labtest = newLabel ()
        let labend = newLabel ()
        let lablist = labend :: labtest :: lablist

        [ Label labbegin ]
        @ cStmt body varEnv funEnv lablist
          @ [ Label labtest ]
            @ cExpr e varEnv funEnv lablist
              @ [ IFNZRO labbegin; Label labend ]
    | For (dec, e, op, body) ->
        let labend = newLabel ()
        let labbegin = newLabel ()
        let labtest = newLabel ()
        let lablist = labend :: labtest :: lablist

        cExpr dec varEnv funEnv lablist
        @ [ INCSP -1; Label labbegin ]
          @ cStmt body varEnv funEnv lablist
            @ [ Label labtest ]
              @ cExpr op varEnv funEnv lablist
                @ [ INCSP -1 ]
                  @ cExpr e varEnv funEnv lablist
                    @ [ IFNZRO labbegin ] @ [ Label labend ]
    | Expr e -> cExpr e varEnv funEnv lablist @ [ INCSP -1 ]
    | Block stmts ->
        let rec loop stmts varEnv =
            match stmts with
            | [] -> (snd varEnv, [])
            | s1 :: sr ->
                let (varEnv1, code1) = cStmtOrDec s1 varEnv funEnv lablist
                let (fdepthr, coder) = loop sr varEnv1
                (fdepthr, code1 @ coder)

        let (fdepthend, code) = loop stmts varEnv

        code @ [ INCSP(snd varEnv - fdepthend) ]

    | Return None -> [ RET(snd varEnv - 1) ]
    | Return (Some e) ->
        cExpr e varEnv funEnv lablist
        @ [ RET(snd varEnv) ]
    | Break ->
        let labbreak = breaklab lablist
        [ GOTO labbreak ]
    | Continue ->
        let labcontinue = continuelab lablist
        [ GOTO labcontinue ]

and cStmtOrDec stmtOrDec (varEnv: VarEnv) (funEnv: FunEnv) (lablist: LabEnv) : VarEnv * instr list =
    match stmtOrDec with
    | Stmt stmt -> (varEnv, cStmt stmt varEnv funEnv lablist)
    | Dec (typ, x) -> allocateWithMsg Locvar (typ, x) varEnv
    | DecAndAssign (typ, x, e) ->
        let (varEnv1, code) = allocateWithMsg Locvar (typ, x) varEnv

        (varEnv1,
         code
         @ (cAccess (AccVar(x)) varEnv1 [] lablist)
           @ (cExpr e varEnv1 [] lablist) @ [ STI; INCSP -1 ])

(* Compiling micro-C expressions:

   * e       is the expression to compile
   * varEnv  is the local and gloval variable environment
   * funEnv  is the global function environment

   Net effect principle: if the compilation (cExpr e varEnv funEnv) of
   expression e returns the instruction sequence instrs, then the
   execution of instrs will leave the rvalue of expression e on the
   stack top (and thus extend the current stack frame with one element).
*)

and cExpr (e: expr) (varEnv: VarEnv) (funEnv: FunEnv) (lablist: LabEnv) : instr list =
    match e with
    | Access acc -> cAccess acc varEnv funEnv lablist @ [ LDI ]
    | Assign (acc, e) ->
        cAccess acc varEnv funEnv lablist
        @ cExpr e varEnv funEnv lablist @ [ STI ]
    | CstI i -> [ CSTI i ]
    | CstF i -> [ CSTF(System.BitConverter.ToInt32(System.BitConverter.GetBytes(i), 0)) ]
    | CstC i -> [ CSTC((int32) (System.BitConverter.ToInt16(System.BitConverter.GetBytes(char (i)), 0))) ]
    | Addr acc -> cAccess acc varEnv funEnv lablist
    | Prim1 (ope, e1) ->
        let rec tmp stat =
            match stat with
            | Access (c) -> c
            | _ -> raise (Failure "access fail")

        cExpr e1 varEnv funEnv lablist
        @ (match ope with
           | "!" -> [ NOT ]
           | "printi" -> [ PRINTI ]
           | "printc" -> [ PRINTC ]
           | "I++" ->
               let ass =
                   Assign(tmp e1, Prim2("+", Access(tmp e1), CstI 1))

               cExpr ass varEnv funEnv lablist @ [ INCSP -1 ]
           | "I--" ->
               let ass =
                   Assign(tmp e1, Prim2("-", Access(tmp e1), CstI 1))

               cExpr ass varEnv funEnv lablist @ [ INCSP -1 ]
           | "++I" ->
               let ass =
                   Assign(tmp e1, Prim2("+", Access(tmp e1), CstI 1))

               CSTI 1 :: ADD :: [ INCSP -1 ]
               @ cExpr ass varEnv funEnv lablist
           | "--I" ->
               let ass =
                   Assign(tmp e1, Prim2("-", Access(tmp e1), CstI 1))

               CSTI 1 :: SUB :: [ INCSP -1 ]
               @ cExpr ass varEnv funEnv lablist
           | _ -> raise (Failure "unknown primitive 1"))
    | Prim2 (ope, e1, e2) ->
        cExpr e1 varEnv funEnv lablist
        @ cExpr e2 varEnv funEnv lablist
          @ (match ope with
             | "*" -> [ MUL ]
             | "+" -> [ ADD ]
             | "-" -> [ SUB ]
             | "/" -> [ DIV ]
             | "%" -> [ MOD ]
             | "==" -> [ EQ ]
             | "!=" -> [ EQ; NOT ]
             | "<" -> [ LT ]
             | ">=" -> [ LT; NOT ]
             | ">" -> [ SWAP; LT ]
             | "<=" -> [ SWAP; LT; NOT ]
             | _ -> raise (Failure "unknown primitive 2"))
    | Prim3 (cond, e1, e2) ->
        let labend = newLabel ()
        let labelse = newLabel ()

        cExpr cond varEnv funEnv lablist
        @ [ IFZERO labelse ]
          @ cExpr e1 varEnv funEnv lablist
            @ [ GOTO labend ]
              @ [ Label labelse ]
                @ cExpr e2 varEnv funEnv lablist @ [ Label labend ]
    | Andalso (e1, e2) ->
        let labend = newLabel ()
        let labfalse = newLabel ()

        cExpr e1 varEnv funEnv lablist
        @ [ IFZERO labfalse ]
          @ cExpr e2 varEnv funEnv lablist
            @ [ GOTO labend
                Label labfalse
                CSTI 0
                Label labend ]
    | Orelse (e1, e2) ->
        let labend = newLabel ()
        let labtrue = newLabel ()

        cExpr e1 varEnv funEnv lablist
        @ [ IFNZRO labtrue ]
          @ cExpr e2 varEnv funEnv lablist
            @ [ GOTO labend
                Label labtrue
                CSTI 1
                Label labend ]
    | Call (f, es) -> callfun f es varEnv funEnv

(* Generate code to access variable, dereference pointer or index array.
   The effect of the compiled code is to leave an lvalue on the stack.   *)

and cAccess access (varEnv: VarEnv) (funEnv: FunEnv) (lablist: LabEnv) : instr list =
    match access with
    | AccVar x ->
        match lookup (fst varEnv) x with
        // x86 虚拟机指令 需要知道是全局变量 [GVAR addr]
        // 栈式虚拟机Stack VM 的全局变量的地址是 栈上的偏移 用 [CSTI addr] 表示
        | Glovar addr, _ ->
            if !isX86Instr then
                [ GVAR addr ]
            else
                [ CSTI addr ]
        | Locvar addr, _ -> [ GETBP; OFFSET addr; ADD ]
    | AccDeref e ->
        match e with
        | Access _ -> (cExpr e varEnv funEnv lablist)
        | Addr _ -> (cExpr e varEnv funEnv lablist)
        | _ ->
            printfn "WARN: x86 pointer arithmetic not support!"
            (cExpr e varEnv funEnv lablist)
    | AccIndex (acc, idx) ->
        cAccess acc varEnv funEnv lablist
        @ [ LDI ]
          @ x86patch (cExpr idx varEnv funEnv lablist)
            @ [ ADD ]

(* Generate code to evaluate a list es of expressions: *)

and cExprs es varEnv funEnv : instr list =
    List.concat (List.map (fun e -> cExpr e varEnv funEnv []) es)

(* Generate code to evaluate arguments es and then call function f: *)

and callfun f es varEnv funEnv : instr list =
    let (labf, tyOpt, paramdecs) = lookup funEnv f
    let argc = List.length es

    if argc = List.length paramdecs then
        cExprs es varEnv funEnv @ [ CALL(argc, labf) ]
    else
        raise (Failure(f + ": parameter/argument mismatch"))

(* Compile a complete micro-C and write the resulting instruction list
   to file fname; also, return the program as a list of instructions.
 *)

let intsToFile (inss: int list) (fname: string) =
    File.WriteAllText(fname, String.concat " " (List.map string inss))

let writeInstr fname instrs =
    let ins =
        String.concat "\n" (List.map string instrs)

    File.WriteAllText(fname, ins)
    printfn $"VM instructions saved in file:\n\t{fname}"

(* ------------------------------------------------------------------- *)


(* Build environments for global variables and functions *)

let makeGlobalEnvs (topdecs: topdec list) : VarEnv * FunEnv * instr list =
    let rec addv decs varEnv funEnv =
        msg $"\nGlobal funEnv:\n{funEnv}\n"

        match decs with
        | [] -> (varEnv, funEnv, [])
        | dec :: decr ->
            match dec with
            | Vardec (typ, var) ->
                let (varEnv1, code1) = allocateWithMsg Glovar (typ, var) varEnv
                let (varEnvr, funEnvr, coder) = addv decr varEnv1 funEnv
                (varEnvr, funEnvr, code1 @ coder)
            | Fundec (tyOpt, f, xs, body) -> addv decr varEnv ((f, ($"{newLabel ()}_{f}", tyOpt, xs)) :: funEnv)
            | VardecAndAssign (typ, var, e) ->
                let (varEnv1, code1) = allocateWithMsg Glovar (typ, var) varEnv
                let (varEnvr, funEnvr, coder) = addv decr varEnv1 funEnv

                (varEnvr,
                 funEnvr,
                 code1
                 @ coder
                   @ (cAccess (AccVar(var)) varEnvr funEnvr [])
                     @ (cExpr e varEnv funEnv []) @ [ STI; INCSP -1 ])

    addv topdecs ([], 0) []


(* Compile a complete micro-C program: globals, call to main, functions *)
let argc = ref 0

let cProgram (Prog topdecs) : instr list =
    let _ = resetLabels ()
    let ((globalVarEnv, _), funEnv, globalInit) = makeGlobalEnvs topdecs

    let compilefun (tyOpt, f, xs, body) =
        let (labf, _, paras) = lookup funEnv f
        let paraNums = List.length paras
        let (envf, fdepthf) = bindParams paras (globalVarEnv, 0)
        let code = cStmt body (envf, fdepthf) funEnv []

        [ FLabel(paraNums, labf) ]
        @ code @ [ RET(paraNums - 1) ]

    let functions =
        List.choose
            (function
            | Fundec (rTy, name, argTy, body) -> Some(compilefun (rTy, name, argTy, body))
            | Vardec _ -> None
            | VardecAndAssign _ -> None)
            topdecs

    let (mainlab, _, mainparams) = lookup funEnv "main"
    argc := List.length mainparams

    globalInit
    @ [ LDARGS !argc
        CALL(!argc, mainlab)
        STOP ]
      @ List.concat functions


let compileToFile program fname =

    msg <| sprintf "program:\n %A" program

    let instrs = cProgram program

    msg <| sprintf "\nStack VM instrs:\n %A\n" instrs

    writeInstr (fname + ".ins") instrs

    let bytecode = code2ints instrs

    msg
    <| sprintf "Stack VM numeric code:\n %A\n" bytecode

    // 面向 x86 的虚拟机指令 略有差异，主要是地址偏移的计算方式不同
    // 单独生成 x86 的指令
    isX86Instr := true
    let x86instrs = cProgram program
    writeInstr (fname + ".insx86") x86instrs

    let x86asmlist = List.map emitx86 x86instrs

    let x86asmbody =
        List.fold (fun asm ins -> asm + ins) "" x86asmlist

    let x86asm =
        (x86header + beforeinit !argc + x86asmbody)

    printfn $"x86 assembly saved in file:\n\t{fname}.asm"
    File.WriteAllText(fname + ".asm", x86asm)

    // let deinstrs = decomp bytecode
    // printf "deinstrs: %A\n" deinstrs
    intsToFile bytecode (fname + ".out")

    instrs

(* Example programs are found in the files ex1.c, ex2.c, etc *)
