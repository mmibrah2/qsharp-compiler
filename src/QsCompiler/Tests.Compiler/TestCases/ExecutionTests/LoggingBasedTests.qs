﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

/// This namespace contains test cases for tests based on logging during execution
namespace Microsoft.Quantum.Testing.ExecutionTests {

    open Microsoft.Quantum.Intrinsic;


    operation ULog<'T> (i : 'T) : Unit {
        body (...) {
            Message($"{i}");
        }
        adjoint (...) {
            Message($"Adjoint {i}");
        }
        controlled (cs, ...) {
            Message($"Controlled {i}");
        }
        controlled adjoint (cs, ...) {
            Message($"Controlled Adjoint {i}");
        }
    }


    // tests related to auto-generation of functor specializations for operations involving conjugations

    operation SpecGenForConjugations () : Unit 
    is Adj + Ctl {

        within {
            ULog("U1");
            ULog("V1");

            within {
                let dummy = 0;
                ULog("U3");
                ULog("V3");
            }
            apply {
                let dummy = 0;
                ULog("Core3");
            }
        }
        apply {
            ULog("Core1");

            within {
                ULog("U2");
                ULog("V2");
            }
            apply {
                ULog("Core2");
            }
        }
    }

    operation ConjugationsInBody () : Unit {
        SpecGenForConjugations();
    }

    operation ConjugationsInAdjoint () : Unit {
        Adjoint SpecGenForConjugations();
    }

    operation ConjugationsInControlled () : Unit {
        Controlled SpecGenForConjugations(new Qubit[0], ());
    }

    operation ConjugationsInControlledAdjoint () : Unit {
        Controlled Adjoint SpecGenForConjugations(new Qubit[0], ());
    }


    // tests for loading via test names

    operation LogViaTestName () : Unit {
        Library2.Log(0, "nothing");
    }

}