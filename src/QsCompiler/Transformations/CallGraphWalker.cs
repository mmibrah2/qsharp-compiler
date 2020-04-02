﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Quantum.QsCompiler.DataTypes;
using Microsoft.Quantum.QsCompiler.SyntaxTokens;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using Microsoft.Quantum.QsCompiler.Transformations.Core;


namespace Microsoft.Quantum.QsCompiler.Transformations.CallGraphWalker
{
    using ExpressionKind = QsExpressionKind<TypedExpression, Identifier, ResolvedType>;
    using ResolvedTypeKind = QsTypeKind<ResolvedType, UserDefinedType, QsTypeParameter, CallableInformation>;

    internal class JohnsonCycleFind
    {
        private Stack<(HashSet<int> SCC, int MinNode)> SccStack = new Stack<(HashSet<int> SCC, int MinNode)>();

        public List<List<int>> GetAllCycles(Dictionary<int, List<int>> graph)
        {
            var cycles = new List<List<int>>();

            PushSCCs(graph);
            while (SccStack.Any())
            {
                var (scc, startNode) = SccStack.Pop();
                var subGraph = GetSubGraph(graph, scc);
                cycles.AddRange(GetSccCycles(subGraph, startNode));

                subGraph.Remove(startNode);
                foreach (var (_, successors) in subGraph)
                {
                    successors.Remove(startNode);
                }

                PushSCCs(subGraph);
            }

            return cycles;
        }

        private void PushSCCs(Dictionary<int, List<int>> graph)
        {
            var sccs = TarjanSCC(graph).OrderByDescending(x => x.MinNode);
            foreach (var scc in sccs)
            {
                SccStack.Push(scc);
            }
        }

        private Dictionary<int, List<int>> GetSubGraph(Dictionary<int, List<int>> inputGraph, HashSet<int> subGraphNodes) =>
            inputGraph
                .Where(kvp => subGraphNodes.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Where(dep => subGraphNodes.Contains(dep)).ToList());

        private List<(HashSet<int> SCC, int MinNode)> TarjanSCC(Dictionary<int, List<int>> inputGraph)
        {
            var index = 0; // This algorithm needs its own separate indexing of nodes
            var nodeStack = new Stack<int>();
            var nodeInfo = new Dictionary<int, (int Index, int LowLink, bool OnStack)>();
            var SCCs = new List<(HashSet<int> SCC, int MinNode)>();

            void setMinLowLink(int node, int potentialMin)
            {
                var vInfo = nodeInfo[node];
                if (vInfo.LowLink > potentialMin)
                {
                    vInfo.LowLink = potentialMin;
                    nodeInfo[node] = vInfo;
                }
            }

            void strongconnect(int node)
            {
                // Set the depth index for node to the smallest unused index
                nodeStack.Push(node);
                nodeInfo[node] = (index, index, true);
                index += 1;

                // Consider successors of node
                foreach (var successor in inputGraph[node])
                {
                    if (!nodeInfo.ContainsKey(successor))
                    {
                        // Successor has not yet been visited; recurse on it
                        strongconnect(successor);
                        setMinLowLink(node, nodeInfo[successor].LowLink);
                    }
                    else if (nodeInfo[successor].OnStack)
                    {
                        // Successor is in stack and hence in the current SCC
                        // If successor is not in stack, then (node, successor) is an edge pointing to an SCC already found and must be ignored
                        // Note: The next line may look odd - but is correct.
                        // It says successor.index not successor.lowlink; that is deliberate and from the original paper
                        setMinLowLink(node, nodeInfo[successor].Index);
                    }
                }

                // If node is a root node, pop the stack and generate an SCC
                if (nodeInfo[node].LowLink == nodeInfo[node].Index)
                {
                    var scc = new HashSet<int>();

                    var minNode = node;
                    int nodeInScc;
                    do
                    {
                        nodeInScc = nodeStack.Pop();
                        var wInfo = nodeInfo[nodeInScc];
                        wInfo.OnStack = false;
                        nodeInfo[nodeInScc] = wInfo;
                        scc.Add(nodeInScc);

                        // Keep track of minimum node in scc
                        if (minNode > nodeInScc)
                        {
                            minNode = nodeInScc;
                        }

                    } while (node != nodeInScc);
                    SCCs.Add((scc, minNode));
                }
            }

            foreach (var node in inputGraph.Keys)
            {
                if (!nodeInfo.ContainsKey(node))
                {
                    strongconnect(node);
                }
            }

            return SCCs;
        }

        private List<List<int>> GetSccCycles(Dictionary<int, List<int>> intputSCC, int startNode)
        {
            var cycles = new List<List<int>>();
            var blockedSet = new HashSet<int>();
            var blockedMap = new Dictionary<int, HashSet<int>>();
            var nodeStack = new Stack<int>();

            void unblock(int node)
            {
                if (blockedSet.Remove(node) && blockedMap.TryGetValue(node, out var nodesToUnblock))
                {
                    blockedMap.Remove(node);
                    foreach (var n in nodesToUnblock)
                    {
                        unblock(n);
                    }
                }
            }

            bool populateCycles(int currNode)
            {
                var foundCycle = false;
                nodeStack.Push(currNode);
                blockedSet.Add(currNode);

                foreach (var successor in intputSCC[currNode])
                {
                    if (successor == startNode)
                    {
                        foundCycle = true;
                        cycles.Add(nodeStack.Reverse().ToList());
                    }
                    else if (!blockedSet.Contains(successor))
                    {
                        foundCycle |= populateCycles(successor);
                    }
                }

                nodeStack.Pop();

                if (foundCycle)
                {
                    unblock(currNode);
                }
                else
                {
                    // Mark currNode as being blocked on each of its successors
                    // If any of currNode's successors unblock, currNode will unblock
                    foreach (var successor in intputSCC[currNode])
                    {
                        if (!blockedMap.ContainsKey(successor))
                        {
                            blockedMap[successor] = new HashSet<int>() { currNode };
                        }
                        else
                        {
                            blockedMap[successor].Add(currNode);
                        }
                    }
                }

                return foundCycle;
            }

            populateCycles(startNode);

            return cycles;
        }
    }

    /// Class used to track call graph of a compilation
    public class CallGraph
    {
        public struct CallGraphEdge
        {
            public ImmutableDictionary<Tuple<QsQualifiedName, NonNullable<string>>, ResolvedType> ParamResolutions;
        }

        public struct CallGraphNode
        {
            public QsQualifiedName CallableName;
            public QsSpecializationKind Kind;
            public QsNullable<ImmutableArray<ResolvedType>> TypeArgs;
        }

        //public List<ImmutableArray<CallGraphNode>> JohnsonCycleFind()
        //{
        //    var indexToNode = _Dependencies.Keys.ToImmutableArray();
        //    var nodeToIndex = indexToNode.Select((v, i) => (v, i)).ToImmutableDictionary(kvp => kvp.v, kvp => kvp.i);
        //    //var graph = indexToNode.Select(node => _Dependencies[node].Keys.Select(dep => nodeToIndex[dep]).ToList()).ToList();
        //    var graph = indexToNode
        //        .Select((v, i) => (v, i))
        //        .ToDictionary(kvp => kvp.i,
        //            kvp => _Dependencies[kvp.v].Keys
        //                .Select(dep => nodeToIndex[dep])
        //                .ToList());

        //    var sccStack = new Stack<(HashSet<int> SCC, int MinNode)>();

        //    var cycles = new List<ImmutableArray<CallGraphNode>>();
        //    var blockedSet = new HashSet<int>();
        //    var blockedMap = new Dictionary<int, HashSet<int>>();
        //    var stack = new Stack<int>();

        //    pushSCCs(graph);
        //    while (sccStack.Any())
        //    {
        //        var (scc, startNode) = sccStack.Pop();
        //        var subGraph = getSubGraph(graph, scc);
        //        populateCycles(subGraph, startNode, startNode);

        //        subGraph.Remove(startNode);
        //        foreach (var (_, successors) in subGraph)
        //        {
        //            successors.Remove(startNode);
        //        }

        //        pushSCCs(subGraph);
        //    }

        //    return cycles;

        //    void unblock(int node)
        //    {
        //        if (blockedSet.Remove(node) && blockedMap.TryGetValue(node, out var nodesToUnblock))
        //        {
        //            blockedMap.Remove(node);
        //            foreach (var n in nodesToUnblock)
        //            {
        //                unblock(n);
        //            }
        //        }
        //    }

        //    bool populateCycles(Dictionary<int, List<int>> graph, int startNode, int currNode)
        //    {
        //        var foundCycle = false;
        //        stack.Push(currNode);
        //        blockedSet.Add(currNode);

        //        foreach (var successor in graph[currNode])
        //        {
        //            if (successor == startNode)
        //            {
        //                foundCycle = true;
        //                cycles.Add(stack.Select(index => indexToNode[index]).Reverse().ToImmutableArray());
        //            }
        //            else if (!blockedSet.Contains(successor))
        //            {
        //                foundCycle |= populateCycles(graph, startNode, successor);
        //            }
        //        }

        //        stack.Pop();

        //        if (foundCycle)
        //        {
        //            unblock(currNode);
        //        }
        //        else
        //        {
        //            // Mark currNode as being blocked on each of its successors
        //            // If any of currNode's successors unblock, currNode will unblock
        //            foreach (var successor in graph[currNode])
        //            {
        //                if (!blockedMap.ContainsKey(successor))
        //                {
        //                    blockedMap[successor] = new HashSet<int>() { currNode };
        //                }
        //                else
        //                {
        //                    blockedMap[successor].Add(currNode);
        //                }
        //            }
        //        }

        //        return foundCycle;
        //    }

        //    void pushSCCs(Dictionary<int, List<int>> input)
        //    {
        //        var sccs = otherTarjanSCC(input).OrderByDescending(x => x.MinNode);
        //        foreach (var scc in sccs)
        //        {
        //            sccStack.Push(scc);
        //        }
        //    }
        //}

        //private Dictionary<int, List<int>> getSubGraph(Dictionary<int, List<int>> inputGraph, HashSet<int> subGraphNodes) =>
        //    inputGraph
        //        .Where(kvp => subGraphNodes.Contains(kvp.Key))
        //        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Where(dep => subGraphNodes.Contains(dep)).ToList());

        //private List<(HashSet<int> SCC, int MinNode)> otherTarjanSCC(Dictionary<int, List<int>> input)
        //{
        //    var index = 0; // This algorithm needs its own separate indexing of nodes
        //    var nodeStack = new Stack<int>();
        //    var nodeInfo = new Dictionary<int, (int Index, int LowLink, bool OnStack)>();
        //    var output = new List<(HashSet<int> SCC, int MinNode)>();

        //    foreach (var node in input.Keys)
        //    {
        //        if (!nodeInfo.ContainsKey(node))
        //        {
        //            strongconnect(node);
        //        }
        //    }

        //    void setMinLowLink(int node, int potentialMin)
        //    {
        //        var vInfo = nodeInfo[node];
        //        if (vInfo.LowLink > potentialMin)
        //        {
        //            vInfo.LowLink = potentialMin;
        //            nodeInfo[node] = vInfo;
        //        }
        //    }

        //    void strongconnect(int node)
        //    {
        //        // Set the depth index for node to the smallest unused index
        //        nodeStack.Push(node);
        //        nodeInfo[node] = (index, index, true);
        //        index += 1;

        //        // Consider successors of node
        //        foreach (var successor in input[node])
        //        {
        //            if (!nodeInfo.ContainsKey(successor))
        //            {
        //                // Successor has not yet been visited; recurse on it
        //                strongconnect(successor);
        //                setMinLowLink(node, nodeInfo[successor].LowLink);
        //            }
        //            else if (nodeInfo[successor].OnStack)
        //            {
        //                // Successor is in stack and hence in the current SCC
        //                // If successor is not in stack, then (node, successor) is an edge pointing to an SCC already found and must be ignored
        //                // Note: The next line may look odd - but is correct.
        //                // It says successor.index not successor.lowlink; that is deliberate and from the original paper
        //                setMinLowLink(node, nodeInfo[successor].Index);
        //            }
        //        }

        //        // If node is a root node, pop the stack and generate an SCC
        //        if (nodeInfo[node].LowLink == nodeInfo[node].Index)
        //        {
        //            var scc = new HashSet<int>();

        //            var minNode = node;
        //            int nodeInScc;
        //            do
        //            {
        //                nodeInScc = nodeStack.Pop();
        //                var wInfo = nodeInfo[nodeInScc];
        //                wInfo.OnStack = false;
        //                nodeInfo[nodeInScc] = wInfo;
        //                scc.Add(nodeInScc);

        //                // Keep track of minimum node in scc
        //                if (minNode > nodeInScc)
        //                {
        //                    minNode = nodeInScc;
        //                }

        //            } while (node != nodeInScc);
        //            output.Add((scc, minNode));
        //        }
        //    }

        //    return output;
        //}

        /// <summary>
        /// This is a dictionary mapping source nodes to information about target nodes. This information is represented
        /// by a dictionary mapping target node to the edges pointing from the source node to the target node.
        /// </summary>
        private Dictionary<CallGraphNode, Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>>> _Dependencies =
            new Dictionary<CallGraphNode, Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>>>();

        private QsNullable<ImmutableArray<ResolvedType>> RemovePositionFromTypeArgs(QsNullable<ImmutableArray<ResolvedType>> tArgs) =>
            tArgs.IsValue
            ? QsNullable<ImmutableArray<ResolvedType>>.NewValue(tArgs.Item.Select(x => StripPositionInfo.Apply(x)).ToImmutableArray())
            : tArgs;

        /// <summary>
        /// This is Tarjan's algorithm for finding all strongly-connected components in a graph.
        /// A strongly-connected component, or SCC, is a subgraph in which all nodes can reach
        /// all other nodes.
        ///
        /// This implementation was based on the pseudo-code found here:
        /// https://en.wikipedia.org/wiki/Tarjan%27s_strongly_connected_components_algorithm
        ///
        /// This returns a list of SCCs, each represented as a set of nodes. The list is
        /// sorted such that each SCC comes before any of its successors (reverse topological ordering).
        /// </summary>
        private List<HashSet<CallGraphNode>> TarjanSCC()
        {
            var index = 0;
            var s = new Stack<CallGraphNode>();
            var nodeInfo = new Dictionary<CallGraphNode, (int Index, int LowLink, bool OnStack)>();
            var output = new List<HashSet<CallGraphNode>>();

            foreach (var v in this._Dependencies.Keys)
            {
                if (!nodeInfo.ContainsKey(v))
                {
                    strongconnect(v);
                }
            }

            void setMinLowLink(CallGraphNode v, int potentialMin)
            {
                var vInfo = nodeInfo[v];
                if (vInfo.LowLink > potentialMin)
                {
                    vInfo.LowLink = potentialMin;
                    nodeInfo[v] = vInfo;
                }
            }

            void strongconnect(CallGraphNode v)
            {
                // Set the depth index for v to the smallest unused index
                s.Push(v);
                nodeInfo[v] = (index, index, true);
                index += 1;

                // Consider successors of v
                foreach (var w in this._Dependencies[v].Keys)
                {
                    if (!nodeInfo.ContainsKey(w))
                    {
                        // Successor w has not yet been visited; recurse on it
                        strongconnect(w);
                        setMinLowLink(v, nodeInfo[w].LowLink);
                    }
                    else if (nodeInfo[w].OnStack)
                    {
                        // Successor w is in stack S and hence in the current SCC
                        // If w is not on stack, then (v, w) is an edge pointing to an SCC already found and must be ignored
                        // Note: The next line may look odd - but is correct.
                        // It says w.index not w.lowlink; that is deliberate and from the original paper
                        setMinLowLink(v, nodeInfo[w].Index);
                    }
                }

                // If v is a root node, pop the stack and generate an SCC
                if (nodeInfo[v].LowLink == nodeInfo[v].Index)
                {
                    var scc = new HashSet<CallGraphNode>();

                    CallGraphNode w;
                    do
                    {
                        w = s.Pop();
                        var wInfo = nodeInfo[w];
                        wInfo.OnStack = false;
                        nodeInfo[w] = wInfo;
                        scc.Add(w);
                    } while (!v.Equals(w));
                    output.Add(scc);
                }
            }

            return output;
        }

        private void RecordDependency(CallGraphNode callerKey, CallGraphNode calledKey, CallGraphEdge edge)
        {
            if (_Dependencies.TryGetValue(callerKey, out var deps))
            {
                if (deps.TryGetValue(calledKey, out var edges))
                {
                    deps[calledKey] = edges.Add(edge);
                }
                else
                {
                    deps[calledKey] = ImmutableArray.Create(edge);
                }
            }
            else
            {
                var newDeps = new Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>>();
                newDeps[calledKey] = ImmutableArray.Create(edge);
                _Dependencies[callerKey] = newDeps;
            }

            // Need to make sure the Dependencies has an entry for each node in the graph, even if node has no dependencies
            if (!_Dependencies.ContainsKey(calledKey))
            {
                _Dependencies[calledKey] = new Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>>();
            }
        }

        /// <summary>
        /// Adds a dependency to the call graph using the caller's specialization and the called specialization's information.
        /// </summary>
        public void AddDependency(QsSpecialization callerSpec, QsQualifiedName calledName, QsSpecializationKind calledKind, QsNullable<ImmutableArray<ResolvedType>> calledTypeArgs, CallGraphEdge edge) =>
            AddDependency(
                callerSpec.Parent, callerSpec.Kind, callerSpec.TypeArguments,
                calledName, calledKind, calledTypeArgs,
                edge);

        /// <summary>
        /// Adds a dependency to the call graph using the relevant information from the caller's specialization and the called specialization.
        /// </summary>
        public void AddDependency(
            QsQualifiedName callerName, QsSpecializationKind callerKind, QsNullable<ImmutableArray<ResolvedType>> callerTypeArgs,
            QsQualifiedName calledName, QsSpecializationKind calledKind, QsNullable<ImmutableArray<ResolvedType>> calledTypeArgs,
            CallGraphEdge edge)
        {
            // ToDo: Setting TypeArgs to Null because the type specialization is not implemented yet
            var callerKey = new CallGraphNode { CallableName = callerName, Kind = callerKind, TypeArgs = QsNullable<ImmutableArray<ResolvedType>>.Null };
            var calledKey = new CallGraphNode { CallableName = calledName, Kind = calledKind, TypeArgs = QsNullable<ImmutableArray<ResolvedType>>.Null };

            RecordDependency(callerKey, calledKey, edge);
        }

        /// <summary>
        /// Returns all specializations that are used directly within the given caller, whether they are
        /// called, partially applied, or assigned. Each key in the returned dictionary represents a
        /// specialization that is used by the caller. Each value in the dictionary is an array of edges
        /// representing all the different ways the given caller specialization took a dependency on the
        /// specialization represented by the associated key.
        /// </summary>
        public Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>> GetDirectDependencies(CallGraphNode callerSpec)
        {
            if (_Dependencies.TryGetValue(callerSpec, out var deps))
            {
                return deps;
            }
            else
            {
                return new Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>>();
            }
        }

        /// <summary>
        /// Returns all specializations that are used directly within the given caller, whether they are
        /// called, partially applied, or assigned. Each key in the returned dictionary represents a
        /// specialization that is used by the caller. Each value in the dictionary is an array of edges
        /// representing all the different ways the given caller specialization took a dependency on the
        /// specialization represented by the associated key.
        /// </summary>
        public Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>> GetDirectDependencies(QsSpecialization callerSpec) =>
            GetDirectDependencies(new CallGraphNode { CallableName = callerSpec.Parent, Kind = callerSpec.Kind, TypeArgs = RemovePositionFromTypeArgs(callerSpec.TypeArguments) });

        // ToDo: this method needs a way of resolving type parameters before it can be completed
        public Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>> GetAllDependencies(CallGraphNode callerSpec)
        {
            return new Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>>();

            //HashSet<(CallGraphNode, CallGraphEdge)> WalkDependencyTree(CallGraphNode root, HashSet<(CallGraphNode, CallGraphEdge)> accum, DependencyType parentDepType)
            //{
            //    if (_Dependencies.TryGetValue(root, out var next))
            //    {
            //        foreach (var k in next)
            //        {
            //            // Get the maximum type of dependency between the parent dependency type and the current dependency type
            //            var maxDepType = k.Item2.CompareTo(parentDepType) > 0 ? k.Item2 : parentDepType;
            //            if (accum.Add((k.Item1, maxDepType)))
            //            {
            //                // ToDo: this won't work once Type specialization are implemented
            //                var noTypeParams = new CallGraphNode { CallableName = k.Item1.CallableName, Kind = k.Item1.Kind, TypeArgs = QsNullable<ImmutableArray<ResolvedType>>.Null };
            //                WalkDependencyTree(noTypeParams, accum, maxDepType);
            //            }
            //        }
            //    }
            //
            //    return accum;
            //}
            //
            //return WalkDependencyTree(callerSpec, new HashSet<(CallGraphNode, DependencyType)>(), DependencyType.NoTypeParameters).ToImmutableArray();
        }

        /// <summary>
        /// Returns all specializations that are used directly or indirectly within the given caller,
        /// whether they are called, partially applied, or assigned. Each key in the returned dictionary
        /// represents a specialization that is used by the caller. Each value in the dictionary is an
        /// array of edges representing all the different ways the given caller specialization took a
        /// dependency on the specialization represented by the associated key.
        /// </summary>
        public Dictionary<CallGraphNode, ImmutableArray<CallGraphEdge>> GetAllDependencies(QsSpecialization callerSpec) =>
            GetAllDependencies(new CallGraphNode { CallableName = callerSpec.Parent, Kind = callerSpec.Kind, TypeArgs = RemovePositionFromTypeArgs(callerSpec.TypeArguments) });

        /// <summary>
        /// Finds and returns a list of all cycles in the call graph, each one being represented by an array of nodes.
        /// To get the edges between the nodes of a given cycle, use the GetDirectDependencies method.
        /// </summary>
        public List<ImmutableArray<CallGraphNode>> GetCallCycles()
        {
            var indexToNode = _Dependencies.Keys.ToImmutableArray();
            var nodeToIndex = indexToNode.Select((v, i) => (v, i)).ToImmutableDictionary(kvp => kvp.v, kvp => kvp.i);
            var graph = indexToNode
                .Select((v, i) => (v, i))
                .ToDictionary(kvp => kvp.i,
                    kvp => _Dependencies[kvp.v].Keys
                        .Select(dep => nodeToIndex[dep])
                        .ToList());

            var cycles = new JohnsonCycleFind().GetAllCycles(graph);
            return cycles.Select(cycle => cycle.Select(index => indexToNode[index]).ToImmutableArray()).ToList();
        }

        //public List<ImmutableArray<CallGraphNode>> GetCallCycles()
        //{
        //    var callStack = new Dictionary<CallGraphNode, CallGraphNode>();
        //    //var finished = new HashSet<CallGraphNode>();
        //    var cycles = new List<ImmutableArray<CallGraphNode>>();

        //    void processDependencies(CallGraphNode node)
        //    {
        //        if (_Dependencies.TryGetValue(node, out var dependencies))
        //        {
        //            foreach (var temp in dependencies)
        //            {
        //                var (curr, _) = temp;

        //                //if (!finished.Contains(curr))
        //                //{
        //                    //if (callStack.ContainsKey(curr))
        //                    //{
        //                    //    // Cycle detected
        //                    //
        //                    //    var cycle = new List<CallGraphNode>() { curr };
        //                    //    while (callStack.TryGetValue(curr, out var next))
        //                    //    {
        //                    //        if (curr.Equals(next)) break;
        //                    //        cycle.Add(next);
        //                    //        curr = next;
        //                    //    }
        //                    //
        //                    //    cycles.Add(cycle.ToImmutableArray());
        //                    //}
        //                    if (curr.Equals(node) || callStack.ContainsKey(curr))
        //                    {
        //                        // Cycle detected

        //                        var cycle = new List<CallGraphNode>() { curr };
        //                        while (callStack.TryGetValue(curr, out var next))
        //                        {
        //                            //if (curr.Equals(next)) break;
        //                            cycle.Add(next);
        //                            curr = next;
        //                        }

        //                        cycles.Add(cycle.ToImmutableArray());
        //                    }
        //                    else
        //                    {
        //                        callStack[node] = curr;
        //                        processDependencies(curr);
        //                        callStack.Remove(node);
        //                    }
        //                //}
        //            }
        //        }

        //        //finished.Add(node);
        //    }

        //    // Loop over all nodes in the call graph, attempting to find cycles by processing their dependencies
        //    foreach (var node in _Dependencies.Keys)
        //    {
        //        //if (!finished.Contains(node))
        //        //{
        //            processDependencies(node);
        //        //}
        //    }

        //    return cycles;
        //}
    }

    /// <summary>
    /// This transformation walks through the compilation without changing it, building up a call graph as it does.
    /// This call graph is then returned to the user.
    /// </summary>
    public static class BuildCallGraph
    {
        public static CallGraph Apply(QsCompilation compilation)
        {
            var walker = new BuildGraph();

            foreach (var ns in compilation.Namespaces)
            {
                walker.Namespaces.OnNamespace(ns);
            }

            return walker.SharedState.graph;
        }

        private class BuildGraph : SyntaxTreeTransformation<BuildGraph.TransformationState>
        {
            public class TransformationState
            {
                internal QsSpecialization spec;

                internal bool inCall = false;
                internal bool hasAdjointDependency = false;
                internal bool hasControlledDependency = false;

                internal CallGraph graph = new CallGraph();
            }

            public BuildGraph() : base(new TransformationState())
            {
                this.Namespaces = new NamespaceTransformation(this);
                this.Statements = new StatementTransformation<TransformationState>(this, TransformationOptions.NoRebuild);
                this.StatementKinds = new StatementKindTransformation<TransformationState>(this, TransformationOptions.NoRebuild);
                this.Expressions = new ExpressionTransformation<TransformationState>(this, TransformationOptions.NoRebuild);
                this.ExpressionKinds = new ExpressionKindTransformation(this);
                this.Types = new TypeTransformation<TransformationState>(this, TransformationOptions.Disabled);
            }

            private class NamespaceTransformation : NamespaceTransformation<TransformationState>
            {
                public NamespaceTransformation(SyntaxTreeTransformation<TransformationState> parent) : base(parent, TransformationOptions.NoRebuild) { }

                public override QsSpecialization OnSpecializationDeclaration(QsSpecialization spec)
                {
                    SharedState.spec = spec;
                    return base.OnSpecializationDeclaration(spec);
                }
            }

            private class ExpressionKindTransformation : ExpressionKindTransformation<TransformationState>
            {
                public ExpressionKindTransformation(SyntaxTreeTransformation<TransformationState> parent) : base(parent, TransformationOptions.NoRebuild) { }

                private ExpressionKind HandleCall(TypedExpression method, TypedExpression arg)
                {
                    var contextInCall = SharedState.inCall;
                    SharedState.inCall = true;
                    this.Expressions.OnTypedExpression(method);
                    SharedState.inCall = contextInCall;
                    this.Expressions.OnTypedExpression(arg);
                    return ExpressionKind.InvalidExpr;
                }

                public override ExpressionKind OnOperationCall(TypedExpression method, TypedExpression arg) => HandleCall(method, arg);

                public override ExpressionKind OnFunctionCall(TypedExpression method, TypedExpression arg) => HandleCall(method, arg);

                public override ExpressionKind OnAdjointApplication(TypedExpression ex)
                {
                    SharedState.hasAdjointDependency = !SharedState.hasAdjointDependency;
                    var rtrn = base.OnAdjointApplication(ex);
                    SharedState.hasAdjointDependency = !SharedState.hasAdjointDependency;
                    return rtrn;
                }

                public override ExpressionKind OnControlledApplication(TypedExpression ex)
                {
                    var contextControlled = SharedState.hasControlledDependency;
                    SharedState.hasControlledDependency = true;
                    var rtrn = base.OnControlledApplication(ex);
                    SharedState.hasControlledDependency = contextControlled;
                    return rtrn;
                }

                public override ExpressionKind OnIdentifier(Identifier sym, QsNullable<ImmutableArray<ResolvedType>> tArgs)
                {
                    if (sym is Identifier.GlobalCallable global)
                    {
                        // ToDo: Type arguments need to be resolved for the whole expression to be accurate, though this will not be needed until type specialization is implemented
                        var typeArgs = tArgs;

                        // ToDo: Type argument dictionaries need to be resolved and set here
                        var edge = new CallGraph.CallGraphEdge { };

                        if (SharedState.inCall)
                        {
                            var kind = QsSpecializationKind.QsBody;
                            if (SharedState.hasAdjointDependency && SharedState.hasControlledDependency)
                            {
                                kind = QsSpecializationKind.QsControlledAdjoint;
                            }
                            else if (SharedState.hasAdjointDependency)
                            {
                                kind = QsSpecializationKind.QsAdjoint;
                            }
                            else if (SharedState.hasControlledDependency)
                            {
                                kind = QsSpecializationKind.QsControlled;
                            }

                            SharedState.graph.AddDependency(SharedState.spec, global.Item, kind, typeArgs, edge);
                        }
                        else
                        {
                            // The callable is being used in a non-call context, such as being
                            // assigned to a variable or passed as an argument to another callable,
                            // which means it could get a functor applied at some later time.
                            // We're conservative and add all 4 possible kinds.
                            SharedState.graph.AddDependency(SharedState.spec, global.Item, QsSpecializationKind.QsBody, typeArgs, edge);
                            SharedState.graph.AddDependency(SharedState.spec, global.Item, QsSpecializationKind.QsControlled, typeArgs, edge);
                            SharedState.graph.AddDependency(SharedState.spec, global.Item, QsSpecializationKind.QsAdjoint, typeArgs, edge);
                            SharedState.graph.AddDependency(SharedState.spec, global.Item, QsSpecializationKind.QsControlledAdjoint, typeArgs, edge);
                        }
                    }

                    return ExpressionKind.InvalidExpr;
                }
            }
        }
    }
}
