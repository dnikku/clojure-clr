﻿/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

/**
 *   Author: David Miller
 **/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
#if CLR2
using Microsoft.Scripting.Ast;
#else
using System.Linq.Expressions;
#endif
using System.Reflection.Emit;
using Microsoft.Scripting.Generation;

namespace clojure.lang.CljCompiler.Ast
{
    class CaseExpr : Expr, MaybePrimitiveExpr
    {
        #region Data

        readonly LocalBindingExpr _expr;
        readonly int _shift, _mask;
        readonly int _low, _high;
        readonly Expr _defaultExpr;
        readonly SortedDictionary<int, Expr> _tests;
        readonly Dictionary<int, Expr> _thens;

        readonly IPersistentMap _sourceSpan;

        readonly Keyword _switchType;
        readonly Keyword _testType;
        readonly IPersistentSet _skipCheck;
        readonly Type _returnType;

        #endregion

        #region Keywords

        static readonly Keyword _compactKey = Keyword.intern(null, "compact");
        static readonly Keyword _sparseKey = Keyword.intern(null, "sparse");
        static readonly Keyword _hashIdentityKey = Keyword.intern(null, "hash-identity");
        static readonly Keyword _hashEquivKey = Keyword.intern(null, "hash-equiv");
        static readonly Keyword _intKey = Keyword.intern(null, "int");

        #endregion

        #region C-tors

        public CaseExpr( IPersistentMap sourceSpan, LocalBindingExpr expr, int shift, int mask, int low, int high, Expr defaultExpr,
                        SortedDictionary<int, Expr> tests, Dictionary<int, Expr> thens, Keyword switchType, Keyword testType, IPersistentSet skipCheck )
        {
            _sourceSpan = sourceSpan;
            _expr = expr;
            _shift = shift;
            _mask = mask;
            _low = low;
            _high = high;
            _defaultExpr = defaultExpr;
            _tests = tests;
            _thens = thens;
            if (switchType != _compactKey && switchType != _sparseKey)
                throw new ArgumentException("Unexpected switch type: " + switchType);
            _switchType = switchType;
            if (testType != _intKey && testType != _hashEquivKey && testType != _hashIdentityKey)
                throw new ArgumentException("Unexpected test type: " + testType);
            _testType = testType;
            _skipCheck = skipCheck;
            ICollection<Expr> returns = new List<Expr>(thens.Values);
            returns.Add(defaultExpr);
            _returnType = Compiler.MaybeClrType(returns);
            if (RT.count(skipCheck) > 0 && RT.booleanCast(RT.WarnOnReflectionVar.deref()))
            {
                RT.errPrintWriter().WriteLine("Performance warning, {0}:{1} - hash collision of some case test constants; if selected, those entries will be tested sequentially.",
                    Compiler.SourcePathVar.deref(),RT.get(sourceSpan,RT.StartLineKey));
            }
        
        }

        #endregion

        #region Type munging

        public bool HasClrType
        {
            get { return _returnType != null; }
        }

        public Type ClrType
        {
            get { return _returnType; }
        }

        #endregion
        
        #region Parsing

        public sealed class Parser : IParser
        {
            //(case* expr shift mask  default map<minhash, [test then]> table-type test-type skip-check?)
            //prepared by case macro and presumed correct
            //case macro binds actual expr in let so expr is always a local,
            //no need to worry about multiple evaluation
            public Expr Parse(ParserContext pcon, object frm)
            {
                ISeq form = (ISeq) frm;

                if (pcon.Rhc == RHC.Eval)
                    return Compiler.Analyze(pcon, RT.list(RT.list(Compiler.FnSym, PersistentVector.EMPTY, form)),"case__"+RT.nextID());

                PersistentVector args = PersistentVector.create(form.next());

                object exprForm = args.nth(0);
                int shift = Util.ConvertToInt(args.nth(1));
                int mask = Util.ConvertToInt(args.nth(2));
                object defaultForm = args.nth(3);
                IPersistentMap caseMap = (IPersistentMap)args.nth(4);
                Keyword switchType = (Keyword)args.nth(5);
                Keyword testType = (Keyword)args.nth(6);
                IPersistentSet skipCheck = RT.count(args) < 8 ? null : (IPersistentSet)args.nth(7);

                ISeq keys = RT.keys(caseMap);
                int low = Util.ConvertToInt(RT.first(keys));
                int high = Util.ConvertToInt(RT.nth(keys,RT.count(keys)-1));
                LocalBindingExpr testexpr = (LocalBindingExpr)Compiler.Analyze(pcon.SetRhc(RHC.Expression),exprForm);


                SortedDictionary<int,Expr> tests = new SortedDictionary<int,Expr>();
                Dictionary<int,Expr> thens = new Dictionary<int,Expr>();

                //testexpr.shouldClear = false;
                //PathNode branch = new PathNode(PATHTYPE.BRANCH, (PathNode) CLEAR_PATH.get());
            
                foreach ( IMapEntry me in caseMap )
                {
                    int minhash = Util.ConvertToInt(me.key());
                    object pair = me.val(); // [test-val then-expr]
                    object first = RT.first(pair);
                    Expr testExpr = testType == _intKey
                        ? NumberExpr.Parse(Util.ConvertToInt(first))
                        : (first == null ? Compiler.NilExprInstance : new ConstantExpr(first));
                        
                    tests[minhash] = testExpr;
                    Expr thenExpr;
                    //try 
                    //{
                    //    Var.pushThreadBindings(
                    //        RT.map(CLEAR_PATH, new PathNode(PATHTYPE.PATH,branch)));
                    thenExpr = Compiler.Analyze(pcon,RT.second(pair));
                    //}
                    //finally
                    //{
                    //    Var.popThreadBindings();
                    //}
                    thens[minhash] = thenExpr;
                }
            
                Expr defaultExpr;
                //try 
                //{
                //    Var.pushThreadBindings(
                //        RT.map(CLEAR_PATH, new PathNode(PATHTYPE.PATH,branch)));
                defaultExpr = Compiler.Analyze(pcon,defaultForm);
                //}
                //finally
                //{
                //    Var.popThreadBindings();
                //}
            return new CaseExpr(
                (IPersistentMap) Compiler.SourceSpanVar.deref(),
                testexpr,
                shift,
                mask,
                low,
                high,
                defaultExpr,
                tests,
                thens,
                switchType,
                testType,
                skipCheck);

            }
        }
        
        #endregion

        #region eval

        public object Eval()
        {
            throw new InvalidOperationException("Can't eval case");
        }

        #endregion

        #region Code generation

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        /// <remarks>
        /// Equivalent to :
        ///    switch (hashed _expr)
        ///    
        ///      case i:  if _expr == _test_i
        ///                 goto end with _then_i
        ///               else goto default
        ///               
        ///      ...
        ///      default:
        ///            (default_label)
        ///             goto end with _default
        ///      end
        ///    end_label:
        ///      
        /// </remarks>
        public Expression GenCode(RHC rhc, ObjExpr objx, GenContext context)
        {
            return GenCode(rhc, objx, context, false);
        }

        Expression GenCode(RHC rhc, ObjExpr objx, GenContext context, bool genUnboxed)
        {
            Type retType = HasClrType ? ClrType : typeof(object);

            LabelTarget defaultLabel = Expression.Label("default");
            LabelTarget endLabel = Expression.Label(retType,"end");

            Type primExprType = Compiler.MaybePrimitiveType(_expr);

            List<SwitchCase> cases = new List<SwitchCase>();

            foreach (KeyValuePair<int, Expr> pair in _tests)
            {
                int i = pair.Key;
                Expr test = pair.Value;

                Expression testExpr;

                if (_testType == _intKey)
                    testExpr = GenTestForInts(objx, context, primExprType, test, genUnboxed);
                else if (RT.booleanCast(RT.contains(_skipCheck, i)))
                    testExpr = Expression.Constant(true,typeof(Boolean));
                else
                    testExpr = GenTestForHashes(objx, context, test, genUnboxed);

                Expression result = GenResult(objx,context,_thens[i],genUnboxed,retType);

                Expression body =
                   Expression.Condition(
                        testExpr,
                        Expression.Return(endLabel,result),
                        Expression.Goto(defaultLabel));

                cases.Add(Expression.SwitchCase(body, Expression.Constant(i)));
            }


            Expression switchTestExpr = _testType == _intKey 
                ? GenTestExprForInts(objx, context, primExprType,defaultLabel) 
                : GenTestExprForHashes(objx, context);


            Expression defaultExpr =
                Expression.Block(
                    Expression.Label(defaultLabel),
                    Expression.Return(endLabel, GenResult(objx,context,_defaultExpr,genUnboxed,retType)));
   
            Expression switchExpr = switchTestExpr is GotoExpression 
                ? defaultExpr // we know the test fails, the test code is GOTO(default)
                : Expression.Switch(switchTestExpr, Expression.Goto(defaultLabel), cases.ToArray<SwitchCase>());


            Expression block =
                Expression.Block(retType,
                    switchExpr,
                    defaultExpr,
                    Expression.Label(endLabel, Expression.Default(retType)));

            block = Compiler.MaybeAddDebugInfo(block, _sourceSpan, context.IsDebuggable);
            return block;
        }

        private Expression GenTestExprForInts(ObjExpr objx,GenContext context,Type exprType, LabelTarget defaultLabel)
        {
            if (exprType == null)
            {
                if (RT.booleanCast(RT.WarnOnReflectionVar.deref()))
                {
                    RT.errPrintWriter().WriteLine("Performance warning, {0}:{1} - case has int tests, but tested expression is not primitive.",
                        Compiler.SourcePathVar.deref(), RT.get(_sourceSpan, RT.StartLineKey));
                }
                
                ParameterExpression parm = Expression.Parameter(typeof(object),"p");
                Expression initParm = Expression.Assign(parm,_expr.GenCode(RHC.Expression, objx, context));
                Expression testType = Expression.Call(null,Compiler.Method_Util_IsNonCharNumeric,parm);          // matching JVM's handling of char as non-integer
                Expression expr = GenShiftMask(Expression.Call(null,Compiler.Method_Util_ConvertToInt,parm));
                return Expression.Block(
                    new ParameterExpression[] { parm },
                    initParm,
                    Expression.IfThen(Expression.Not(testType), Expression.Goto(defaultLabel)),
                    expr);
            }
            else if (exprType == typeof(long) || exprType == typeof(int) || exprType == typeof(short) || exprType == typeof(byte) || exprType == typeof(ulong) || exprType == typeof(uint) || exprType == typeof(ushort) || exprType == typeof(sbyte))
            {
                Expression expr = Expression.Convert(_expr.GenCodeUnboxed(RHC.Expression, objx, context),typeof(int));
                return GenShiftMask(expr);
            }
            else
            {
                return Expression.Goto(defaultLabel);
            }
        }

        private Expression GenTestExprForHashes(ObjExpr objx,GenContext context)
        {
            Expression expr = Expression.Call(null, Compiler.Method_Util_hash, Expression.Convert(_expr.GenCode(RHC.Expression, objx, context), typeof(Object)));
            return GenShiftMask(expr);
        }

        bool IsShiftMasked { get { return _mask != 0; } }

        Expression GenShiftMask(Expression expr)
        {
            if ( IsShiftMasked )
                return 
                    Expression.And(
                        Expression.RightShift(
                            expr,
                            Expression.Constant(_shift)),
                        Expression.Constant(_mask));
            return expr;
        }


        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily")]
        private Expression GenTestForInts(ObjExpr objx, GenContext context, Type primExprType, Expr test, bool genUnboxed)
        {
            Expression condCode;

            if (primExprType == null)
            {
                Expression exprCode = _expr.GenCode(RHC.Expression, objx, context);
                Expression testCode = test.GenCode(RHC.Expression, objx, context);
                condCode = Expression.Call(null, Compiler.Method_Util_equiv, Compiler.MaybeBox(exprCode), Compiler.MaybeBox(testCode));

            }
            else if (primExprType == typeof(long) || primExprType == typeof(ulong))
            {
                Expression exprCode = _expr.GenCodeUnboxed(RHC.Expression, objx, context);
                Expression testCode = ((NumberExpr)test).GenCodeUnboxed(RHC.Expression, objx, context);
                condCode = Expression.Equal(exprCode, testCode);
            }
            else if (primExprType == typeof(int) || primExprType == typeof(short) || primExprType == typeof(byte) 
                  || primExprType == typeof(uint) || primExprType == typeof(ushort) || primExprType == typeof(sbyte))
            {
                if (IsShiftMasked)
                {
                    Expression exprCode = Expression.Convert(_expr.GenCodeUnboxed(RHC.Expression, objx, context),typeof(long));
                    Expression testCode = ((NumberExpr)test).GenCodeUnboxed(RHC.Expression, objx, context);
                    condCode = Expression.Equal(exprCode, testCode);
                }
                else
                    condCode = Expression.Constant(true);
            }
            else
            {
                condCode = Expression.Constant(false);
            }
            return condCode;
        }

        private Expression GenTestForHashes(ObjExpr objx, GenContext context, Expr test, bool genUnboxed)
        {
            Expression exprCode = _expr.GenCode(RHC.Expression, objx, context);
            Expression testCode = test.GenCode(RHC.Expression, objx, context);
            Expression condCode = _testType == _hashIdentityKey
                ? (Expression)Expression.Equal(exprCode, testCode)
                : (Expression)Expression.Call(null, Compiler.Method_Util_equiv, Compiler.MaybeBox(exprCode), Compiler.MaybeBox(testCode));
            return condCode;
        }

        private static Expression GenResult(ObjExpr objx, GenContext context, Expr expr, bool genUnboxed, Type retType)
        {
            MaybePrimitiveExpr mbExpr = expr as MaybePrimitiveExpr;
            Expression result = genUnboxed && mbExpr != null
                ? mbExpr.GenCodeUnboxed(RHC.Expression, objx, context)
                : expr.GenCode(RHC.Expression, objx, context);

            if (result.Type != retType)
            {
                if (expr is ThrowExpr)
                {
                    // Fix type on the throw expression
                    UnaryExpression ur = (UnaryExpression)result;
                    result = Expression.Throw(ur.Operand, retType);
                }
                else result = Expression.Convert(result, retType);
            }

           return result;
        }


        public void Emit(RHC rhc, ObjExpr objx, GenContext context)
        {
            DoEmit(rhc, objx, context, false);
        }

        public void DoEmit(RHC rhc, ObjExpr objx, GenContext context, bool emitUnboxed)
        {
            ILGen ilg = context.GetILGen();

            Compiler.MaybeEmitDebugInfo(context, ilg, _sourceSpan);

            Label defaultLabel = ilg.DefineLabel();
            Label endLabel = ilg.DefineLabel();

            SortedDictionary<int, Label> labels = new SortedDictionary<int, Label>();
            foreach (int i in _tests.Keys)
                labels[i] = ilg.DefineLabel();

            // TODO: debug info

            Type primExprType = Compiler.MaybePrimitiveType(_expr);


            if (_testType == _intKey)
                EmitExprForInts(objx, context, primExprType, defaultLabel);
            else
                EmitExprForHashes(objx, context);

            if (_switchType == _sparseKey)
            {
                Label[] la = labels.Values.ToArray<Label>();
                ilg.Emit(OpCodes.Switch, la);
                ilg.Emit(OpCodes.Br, defaultLabel);
            }
            else
            {
                Label[] la = new Label[(_high - _low) + 1];
                for (int i = _low; i <= _high; i++)
                    la[i - _low] = labels.ContainsKey(i) ? labels[i] : defaultLabel;
                ilg.Emit(OpCodes.Ldc_I4, _low);
                ilg.Emit(OpCodes.Sub);
                ilg.Emit(OpCodes.Switch, la);
                ilg.Emit(OpCodes.Br, defaultLabel);
             }

            foreach (int i in labels.Keys)
            {
                ilg.MarkLabel(labels[i]);
                if (_testType == _intKey)
                    EmitThenForInts(objx, context, primExprType, _tests[i], _thens[i], defaultLabel, emitUnboxed);
                else if ((bool)RT.contains(_skipCheck, i))
                    EmitExpr(objx, context, _thens[i], emitUnboxed);
                else
                    EmitThenForHashes(objx, context, _tests[i], _thens[i], defaultLabel, emitUnboxed);
                ilg.Emit(OpCodes.Br, endLabel);
            }
            ilg.MarkLabel(defaultLabel);
            EmitExpr(objx, context, _defaultExpr, emitUnboxed);
            ilg.MarkLabel(endLabel);
            if (rhc == RHC.Statement)
                ilg.Emit(OpCodes.Pop);
        }

        //bool IsShiftMasked { get { return _mask != 0; } }

        void EmitShiftMask(ILGenerator ilg)
        {
            if (IsShiftMasked)
            {
                ilg.Emit(OpCodes.Ldc_I4, _shift);
                ilg.Emit(OpCodes.Shr);
                ilg.Emit(OpCodes.Ldc_I4, _mask);
                ilg.Emit(OpCodes.And);
            }
        }

        private void EmitExprForInts(ObjExpr objx, GenContext context, Type exprType, Label defaultLabel)
        {
            ILGenerator ilg = context.GetILGenerator();

            if (exprType == null)
            {
                if ( RT.booleanCast(RT.WarnOnReflectionVar.deref()))
                {
                    RT.errPrintWriter().WriteLine("Performance warning, {0}:{1} - case has int tests, but tested expression is not primitive.",
                        Compiler.SourcePathVar.deref(),Compiler.GetLineFromSpanMap(_sourceSpan));
                }
                _expr.Emit(RHC.Expression,objx,context);
                ilg.Emit(OpCodes.Call,Compiler.Method_Util_IsNonCharNumeric);
                ilg.Emit(OpCodes.Brfalse,defaultLabel);
                _expr.Emit(RHC.Expression,objx,context);
                ilg.Emit(OpCodes.Call,Compiler.Method_Util_ConvertToInt);
                EmitShiftMask(ilg);
            }
            else if (exprType == typeof(long)
                || exprType == typeof(int)
                || exprType == typeof(short)
                || exprType == typeof(byte)
                || exprType == typeof(ulong)
                || exprType == typeof(uint)
                || exprType == typeof(ushort)
                || exprType == typeof(sbyte))
            {
                _expr.EmitUnboxed(RHC.Expression,objx,context);
                ilg.Emit(OpCodes.Conv_I4);
                EmitShiftMask(ilg);

            }
            else
            {
                ilg.Emit(OpCodes.Br,defaultLabel);
            }
        }

        private void EmitThenForInts(ObjExpr objx, GenContext context, Type exprType, Expr test, Expr then, Label defaultLabel, bool emitUnboxed)
        {
            ILGenerator ilg = context.GetILGenerator();

            if (exprType == null)
            {
                _expr.Emit(RHC.Expression, objx, context);
                test.Emit(RHC.Expression, objx, context);
                ilg.Emit(OpCodes.Call, Compiler.Method_Util_equiv);
                ilg.Emit(OpCodes.Brfalse, defaultLabel);
                EmitExpr(objx, context, then, emitUnboxed);                
            }
            else if (exprType == typeof(long))
            {
                ((NumberExpr)test).EmitUnboxed(RHC.Expression, objx, context);
                _expr.EmitUnboxed(RHC.Expression, objx, context);
                ilg.Emit(OpCodes.Ceq);
                ilg.Emit(OpCodes.Brfalse, defaultLabel);
                EmitExpr(objx, context, then, emitUnboxed);                
              
            }
            else if (exprType == typeof(int)
                || exprType == typeof(short)
                || exprType == typeof(byte)
                || exprType == typeof(ulong)
                || exprType == typeof(uint)
                || exprType == typeof(ushort)
                || exprType == typeof(sbyte))
            {
                if (IsShiftMasked)
                {
                    ((NumberExpr)test).EmitUnboxed(RHC.Expression, objx, context);
                    _expr.EmitUnboxed(RHC.Expression, objx, context);
                    ilg.Emit(OpCodes.Conv_I8);
                    ilg.Emit(OpCodes.Ceq);
                    ilg.Emit(OpCodes.Brfalse, defaultLabel);
                    EmitExpr(objx, context, then, emitUnboxed);
                }
                // else direct match
                EmitExpr(objx, context, then, emitUnboxed);  
            }
            else
            {
                ilg.Emit(OpCodes.Br, defaultLabel);
            }
        }

        void EmitExprForHashes(ObjExpr objx, GenContext context)
        {
            ILGenerator ilg = context.GetILGenerator();

            _expr.Emit(RHC.Expression, objx, context);
            ilg.Emit(OpCodes.Call, Compiler.Method_Util_hash);
            EmitShiftMask(ilg);
        }

        void EmitThenForHashes(ObjExpr objx, GenContext context, Expr test, Expr then, Label defaultLabel, bool emitUnboxed)
        {
            ILGenerator ilg = context.GetILGenerator();

            _expr.Emit(RHC.Expression, objx, context);
            test.Emit(RHC.Expression, objx, context);
            if (_testType == _hashIdentityKey)
            {
                ilg.Emit(OpCodes.Ceq);
                ilg.Emit(OpCodes.Brfalse, defaultLabel);
            }
            else
            {
                ilg.Emit(OpCodes.Call, Compiler.Method_Util_equiv);
                ilg.Emit(OpCodes.Brfalse, defaultLabel);
            }
            EmitExpr(objx, context, then, emitUnboxed);  
        }

        private void EmitExpr(ObjExpr objx, GenContext context, Expr expr, bool emitUnboxed)
        {
            MaybePrimitiveExpr mbe = expr as MaybePrimitiveExpr;
            if (emitUnboxed && mbe != null)
                mbe.EmitUnboxed(RHC.Expression, objx, context);
            else
                expr.Emit(RHC.Expression, objx, context);
        }   


        #endregion

        #region Primitive code generation

        public bool CanEmitPrimitive
        {
            get { return Util.IsPrimitive(_returnType); }
        }

        public Expression GenCodeUnboxed(RHC rhc, ObjExpr objx, GenContext context)
        {
            return GenCode(rhc, objx, context, true);
        }

        public void EmitUnboxed(RHC rhc, ObjExpr objx, GenContext context)
        {
            DoEmit(rhc, objx, context, true);
        }

        #endregion
    }
}
