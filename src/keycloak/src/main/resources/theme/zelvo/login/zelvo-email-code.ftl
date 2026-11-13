<#import "template.ftl" as layout>
<#import "field.ftl" as field>
<#import "buttons.ftl" as buttons>
<@layout.registrationLayout displayMessage=!messagesPerField.existsError('code'); section>
<!-- template: zelvo-email-code.ftl -->
    <#if section="header">
        ${msg("zelvoCodeTitle")}
    <#elseif section="form">
        <p id="zelvo-code-help">${msg("zelvoCodeHelp", email!'')}</p>
        <form id="zelvo-code-form" class="${properties.kcFormClass!}" action="${url.loginAction}" method="post">
            <@field.input name="code" label=msg("zelvoCodeLabel") autocomplete="one-time-code" autofocus=true />
            <@buttons.actionGroup>
                <@buttons.button id="zelvo-sign-in" name="login" label="doLogIn" />
            </@buttons.actionGroup>
        </form>
        <form id="zelvo-code-other" class="${properties.kcFormClass!}" action="${url.loginAction}" method="post">
            <#-- Stacked, full width: long Arabic labels must not overflow on phones. -->
            <@buttons.actionGroup>
                <@buttons.button id="zelvo-resend" name="resend" label="zelvoResendCode" type="secondary" />
                <@buttons.button id="zelvo-back" name="back" label="zelvoUseOtherEmail" type="secondary" />
            </@buttons.actionGroup>
        </form>
    </#if>
</@layout.registrationLayout>
