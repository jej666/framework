﻿#region usings
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Web;
using System.Web.Mvc;
using System.Threading;
using Signum.Services;
using Signum.Utilities;
using Signum.Entities;
using Signum.Web;
using Signum.Engine;
using Signum.Engine.Operations;
using Signum.Engine.Basics;
using Signum.Entities.Basics;
using Signum.Web.Operations;
#endregion

namespace Signum.Web.Controllers
{
    public class OperationController : Controller
    {
        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult Execute(string operationFullKey, bool isLite, string prefix)
        {
            Enum operationKey = OperationClient.GetOperationKeyAssert(operationFullKey);

            IdentifiableEntity entity = null;
            if (isLite)
            {
                Lite<IdentifiableEntity> lite = this.ExtractLite<IdentifiableEntity>(prefix);
                entity = OperationLogic.ExecuteLite<IdentifiableEntity>(lite, operationKey);
            }
            else
            {
                MappingContext context = this.UntypedExtractEntity(prefix).UntypedApplyChanges(this.ControllerContext, prefix, true).UntypedValidateGlobal();
                entity = (IdentifiableEntity)context.UntypedValue;

                if (context.GlobalErrors.Any())
                {
                    this.ModelState.FromContext(context);
                    return JsonAction.ModelState(ModelState);
                }

                entity = OperationLogic.Execute<IdentifiableEntity>(entity, operationKey);
            }

           return OperationClient.DefaultExecuteResult(this, entity, prefix);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult Delete(string operationFullKey, bool isLite, string prefix)
        {
            Enum operationKey = OperationClient.GetOperationKeyAssert(operationFullKey);

            if (isLite)
            {
                Lite<IdentifiableEntity> lite = this.ExtractLite<IdentifiableEntity>(prefix);

                OperationLogic.Delete(lite, operationKey, null);

                return OperationClient.DefaultDelete(this, lite.EntityType);
            }
            else
            {
                MappingContext context = this.UntypedExtractEntity(prefix).UntypedApplyChanges(this.ControllerContext, prefix, true).UntypedValidateGlobal();
                IdentifiableEntity entity = (IdentifiableEntity)context.UntypedValue;

                OperationLogic.Delete(entity, operationKey, null);

                return OperationClient.DefaultDelete(this, entity.GetType());
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult ConstructFrom(string operationFullKey, bool isLite, string prefix, string newPrefix)
        {
            Enum operationKey = OperationClient.GetOperationKeyAssert(operationFullKey);

            IdentifiableEntity entity = null;
            if (isLite)
            {
                Lite<IdentifiableEntity> lite = this.ExtractLite<IdentifiableEntity>(prefix);
                entity = OperationLogic.ConstructFromLite<IdentifiableEntity>(lite, operationKey);
            }
            else
            {
                MappingContext context = this.UntypedExtractEntity(prefix).UntypedApplyChanges(this.ControllerContext, prefix, true).UntypedValidateGlobal();
                entity = (IdentifiableEntity)context.UntypedValue;

                if (context.GlobalErrors.Any())
                {
                    this.ModelState.FromContext(context);
                    return JsonAction.ModelState(ModelState);
                }

                entity = OperationLogic.ConstructFrom<IdentifiableEntity>(entity, operationKey);
            }

            return OperationClient.DefaultConstructResult(this, entity, newPrefix);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult ConstructFromMany(string operationFullKey, string liteKeys, string newPrefix)
        {
            Enum operationKey = OperationClient.GetOperationKeyAssert(operationFullKey);

            var lites = Navigator.ParseLiteKeys<IdentifiableEntity>(liteKeys);

            IdentifiableEntity entity = OperationLogic.ServiceConstructFromMany(lites, lites.First().EntityType, operationKey);

            return OperationClient.DefaultConstructResult(this, entity, newPrefix);
        }
    }
}
