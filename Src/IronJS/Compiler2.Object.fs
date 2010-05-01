﻿namespace IronJS.Compiler

open IronJS
open IronJS.Aliases
open IronJS.Tools
open IronJS.Tools.Dlr
open IronJS.Compiler
open IronJS.Compiler.Types

module Object =

  let properties expr = 
    Expr.field expr "Properties"

  let classId expr = 
    Expr.field expr "ClassId"

  let private buildSet ctx name value target =
    let cache, cacheId, cacheIndex = Runtime.PropertyCache.Create(name)
    Expr.wrapInBlock target (fun obj -> 
      [
        #if DEBUG
        (Expr.constant (sprintf "Setting property '%s'" name))
        #endif
        (Expr.Flow.ternary
          (Expr.Logic.eq (classId obj) cacheId)
          (Utils.Box.assign ctx (Expr.Array.access (properties obj) [cacheIndex]) value.Et)
          (Expr.call cache "UpdateSet" [obj; Utils.Box.wrap value.Et; Context.environmentExpr ctx])
        )
      ]
    )

  let private buildGet ctx name (typ:ClrType option) target next =
    let cache, cacheId, cacheIndex = Runtime.PropertyCache.Create(name)
    Expr.wrapInBlock target (fun obj ->
      let test   = Expr.Logic.eq (classId obj) cacheId
      let cached = (Utils.Box.fieldIfClrType (Expr.Array.access (properties obj) [cacheIndex]) typ)
      let update = (Utils.Box.fieldIfClrType (Expr.call cache "UpdateGet" [obj; Context.environmentExpr ctx]) typ)
      match next with
      | Stub.Done -> [Expr.Flow.ternary test cached update]
      | Stub.Half(target) -> 
        [
          #if DEBUG
          (Expr.constant (sprintf "Getting property '%s'" name))
          #endif
          (Expr.Flow.ternary
            (test)
            (Stub.combineExpr (Expr.static'   cached) next)
            (Stub.combineExpr (Expr.volatile' update) next)
          )
        ]
      | _ -> failwith "Only Stub.Done and Stub.Half allowed"
    )
    
  let unboundSet ctx name value target =
    match target, value with
    | Expr(target), Expr(value) -> 
      if Runtime.Utils.Type.isObject target.Type 
        then Stub.expr (buildSet ctx name value target)
        else 
          if Runtime.Utils.Type.isBox target.Type then
            Stub.expr (
              Expr.wrapInBlock target (fun obj -> 
                [
                  #if DEBUG
                  (Expr.constant (sprintf "Type check for setting property '%s'" name))
                  #endif
                  (Expr.Flow.ternary
                    (Utils.Box.typeIsT<Runtime.Object> obj)
                    (buildSet ctx name value (Expr.static' (Utils.Box.fieldByClrTypeT<Runtime.Object> obj))).Et
                    (Expr.void')
                  )
                ]
              )
            )
          else
            failwith "Dynamic-only object set not supported"
    | _ -> failwith "Failed"

  let unboundGet ctx name typ target next =
    match target with
    | Expr(target) -> 
      if Runtime.Utils.Type.isObject target.Et.Type 
        then Stub.expr (buildGet ctx name typ target next)
        else 
          if Runtime.Utils.Type.isBox target.Type then
            Stub.expr (
              Expr.wrapInBlock target (fun obj -> 
                [
                  #if DEBUG
                  (Expr.constant (sprintf "Type check for getting property '%s'" name))
                  #endif
                  (Expr.Flow.ternary
                    (Utils.Box.typeIsT<Runtime.Object> obj)
                    (buildGet ctx name None (Expr.static' (Utils.Box.fieldByClrTypeT<Runtime.Object> obj)) Done).Et
                    (Expr.defaultT<Runtime.Box>)
                  )
                ]
              )
            )
          else
            failwith "Dynamic-only object set not supported"
    | _ -> failwith "Failed"

  let getProperty ctx target name =
    let unbound = unboundGet ctx name None
    Stub.combine (ctx.Build target) (Stub.third unbound)

  let setProperty ctx name = 
    unboundSet ctx name

  let build (ctx:Context) properties =
    match properties with
    | Some(_) -> failwith "Objects with auto-properties not supported"
    | None    -> 
      let new' = Dlr.Expr.newArgsT<Runtime.Object> [Context.objectBaseClass ctx; Expr.constant 4]
      Stub.expr (Expr.volatile' (new'))