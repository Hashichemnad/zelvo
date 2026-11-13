<#import "template.ftl" as layout>
<#import "buttons.ftl" as buttons>
<@layout.registrationLayout displayMessage=!messagesPerField.existsError('email'); section>
<!-- template: zelvo-email.ftl -->
    <#if section="header">
        ${msg("zelvoEmailTitle")}
    <#elseif section="form">
        <p id="zelvo-email-help">${msg("zelvoEmailHelp")}</p>
        <form id="zelvo-email-form" class="${properties.kcFormClass!}" action="${url.loginAction}" method="post" novalidate>
            <#assign emailError = messagesPerField.get('email')>
            <div class="${properties.kcFormGroupClass}">
                <div class="${properties.kcFormGroupLabelClass}">
                    <label for="email" class="${properties.kcFormLabelClass}">
                        <span class="${properties.kcFormLabelTextClass}">${msg("email")}</span>
                    </label>
                </div>
                <span class="${properties.kcInputClass} <#if emailError?has_content>${properties.kcError}</#if>">
                    <input id="email" name="email" type="email" value="${(email!'')}" autocomplete="email" autofocus dir="ltr"
                           aria-describedby="zelvo-email-help<#if emailError?has_content> input-error-email</#if>"
                           aria-invalid="<#if emailError?has_content>true</#if>"/>
                </span>
                <#if emailError?has_content>
                    <div class="${properties.kcFormHelperTextClass}" aria-live="polite">
                        <span id="input-error-email" class="${properties.kcInputErrorMessageClass}">${kcSanitize(emailError)?no_esc}</span>
                    </div>
                </#if>
            </div>
            <@buttons.actionGroup>
                <@buttons.button id="zelvo-send-code" name="send" label="zelvoSendCode" />
            </@buttons.actionGroup>
        </form>
    </#if>
</@layout.registrationLayout>
